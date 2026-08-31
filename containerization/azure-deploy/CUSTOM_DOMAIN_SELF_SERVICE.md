# Segue / FHIRBridge — Custom domain + SSL self-service runbook

For clients and operators. Fixes the Marketplace/Deploy-to-Azure gap where a single-shot
domain+certificate deploy failed with `RequireCustomHostnameInEnvironment`.

## Why the hostname/cert ordering matters

Azure managed certificates require the **hostname to already exist** on a Container App in the
environment. Creating the cert first fails. Correct order:

1. Register hostname (`bindingType: Disabled`)
2. Create managed cert + bind (`SniEnabled`) — requires step 1 to have already completed

DNS (CNAME + `asuid` TXT) has to be ready **before step 1**, not just before step 2 — Azure
validates the TXT record the moment you try to register the hostname at all, certificate or not.
That's the one thing that can't be automated away: you get the verification ID from Step 1's
outputs (no domain needs to be set for that), create the DNS records yourself, and wait for them to
propagate — but once they have, `custom-domain.bicep` does steps 1 and 2 above **in one deploy**,
sequencing them internally so Azure never sees them out of order.

## Path A — custom-domain.bicep (preferred — deployed on its own, after main.bicep)

`createUiDefinition.json`'s wizard no longer collects domain fields at all (removed 2026-08-28 —
see `backups/2026-08-28-pre-2step-domain-flow`): a Container App's `customDomainVerificationId`
only exists once the app itself does, so asking for a domain on the very first deploy always fails
with `InvalidCustomHostNameValidation`/`RequireCustomHostnameInEnvironment`. Domain binding is now a
**separate template** (`custom-domain.bicep` / compiled `custom-domain.json`), deployed after
`main.bicep`'s step-1 stack is already up. It handles all 3 public-facing apps (FHIRBridge app,
Demo app, Terminology server) in ONE deployment — each with its own optional domain field, none
required, so you can set one, two, or all three in a single run. For each app whose domain is set,
it patches only that container app — reading every other property (env vars, volumes, registries,
existing secrets via `listSecrets`) back from the live resource — so it never requires re-submitting
`main.bicep`'s full parameter set (`sqlSaPassword`, `jwtSigningKey`, image tag, etc.) and can't
accidentally drift them. The write itself is routed through a small module
(`custom-domain-app-update.bicep`) to avoid a genuine ARM circular-dependency (reading + writing the
same container app's `properties`/`listSecrets()` in one template is a self-reference ARM's
validator rejects at deploy time — this doesn't show up under `az deployment group what-if`, only
on a real deploy).

There is ONE Deploy-to-Azure wizard for this template (`createUiDefinition.custom-domain.json`) —
no certificate checkbox at all, and (as of the redesign below) only ONE deploy per domain, not two.
`custom-domain.bicep` always does the full sequence — register hostname, create certificate, bind
it — internally ordered via two module calls (`registerHostnames` then `bindCertificates`, the
second explicitly `dependsOn` the first) so Azure's API only ever sees "certificate created" after
"hostname added" has actually completed, which is the one ordering constraint Azure enforces. The
wizard has a name-prefix field in Basics plus 3 tabs (one per app, `isWizard: true` so Basics must
be completed before any tab renders). Azure shows a one-time "Do you trust the authors code?"
prompt the first time any template using a live API lookup like this is opened — expected, not a
bug, safe to accept.

The one thing that genuinely cannot be folded into this single deploy: **DNS must already be
propagated before you run it at all** — Azure validates the `asuid` TXT record the moment the
hostname is registered, not just at certificate-creation, so there is no way to defer that check to
later within the same deployment. But you don't need a throwaway prior deploy to learn the
verification ID for that TXT record — `main.bicep`'s own outputs
(`fhirbridgeAppDomainVerificationId` / `demoAppDomainVerificationId` /
`hapiTerminologyDomainVerificationId`) already expose it unconditionally, the moment the app
exists, whether or not any domain was ever set on that deploy.

Each tab shows, in order: the app's name; the app's live default URL and domain-verification ID (one
`Microsoft.Solutions.ArmApiControl` GET per tab, parsed out with a string-search expression — see
point 3 below for why that's needed instead of plain property access); the domain textbox; and, once
you type a domain, the exact CNAME/TXT record **names and values** to create.

**Bug history worth knowing if this ever needs revisiting** (three real, independently-diagnosed
bugs stacked on top of each other — worth reading in order if this area breaks again):

1. The live lookups initially came back permanently blank (app name showed fine — pure string
   concat — but default URL/verification ID never did) despite the exact same REST call working via
   `az rest`/`az containerapp show`. Root cause: `resourceGroup().id` was used to build each
   `ArmApiControl` request path, but per Microsoft's own docs `resourceGroup()` only returns
   `{mode, name, location}` — there's no `.id` property, so it silently resolved to nothing and
   every request path was missing its subscription/resource-group prefix entirely. Fixed by building
   the ID explicitly: `concat(subscription().id, '/resourceGroups/', resourceGroup().name, ...)`.
2. After that fix, a browser DevTools Network-tab capture of the actual call showed a response
   shaped like `{"responses": [{"content": {...}}]}` — this is just how Azure's internal batching
   proxy looks at the raw HTTP layer; it is **not** what `ArmApiControl` exposes to expressions.
   Assuming that shape and adding `.responses[0]...`/`first(...)` unwrapping was wrong and broke
   things further (a crash, then a silently-hidden element from a mis-scoped `first()`/`steps()`
   call).
3. The actual remaining bug, confirmed with a deliberate depth-isolation test: `string(steps('step').
   appDetailsApi)` (zero property access after the `steps()` call) reliably dumps the full, correct
   resource body — but `steps('step').appDetailsApi.properties` (just ONE further dot) already
   evaluates to `null`, and stays `null` no matter how the deeper path is written (plain dots, or
   nested `tryGet()` calls). The practical rule this engine enforces: **at most one property access
   is honored immediately after a function-call result like `steps(...)`** — anything chained
   beyond that silently nulls out, in this schema version/control combination at least. There is no
   workaround for reading a specific nested field by property-chaining. The fix that actually works:
   dump the WHOLE control result as one string (`string(steps('step').appDetailsApi)` — zero
   chaining, so it's unaffected by the bug above) and pull a specific field out of that text with
   string-search functions instead of property access — e.g.
   `first(split(last(split(string(steps('step').appDetailsApi), '"fqdn":"')), '"'))` splits on the
   `"fqdn":"` marker, takes everything after it (`last(split(...))`), then cuts it back off at the
   next `"` (`first(split(...))`). This is what each tab's `liveInfo`/`dnsRecords` elements in
   `createUiDefinition.custom-domain.json` use for `fqdn` and `customDomainVerificationId` — string
   operations on the dumped text sidestep the chaining limit entirely, since nothing is being
   accessed as a property. It also closed the earlier security concern: an unprettified raw dump was
   putting plaintext env-var secrets (SQL/Redis passwords) on screen; the string-search approach only
   ever surfaces the two specific fields asked for.
4. Bicep-only syntax sugar (the `x => expr` arrow-lambda short form, `.?prop ?? default`
   safe-navigation) does NOT survive into raw createUiDefinition JSON — that's evaluated by a
   separate, more limited engine, so lambdas must use the explicit
   `lambda('x', ...)`/`lambdaVariables('x')` form and optional-property access must use
   `coalesce(tryGet(obj, 'prop'), default)` (confirmed by inspecting what Bicep itself compiles that
   sugar down to). Moot for the current wizard (no chaining left to need this), but relevant if
   nested-field extraction is ever revisited per point 3.

### Get the DNS values (no deploy needed)

The verification ID and default FQDN exist the moment the app does — no need to deploy
`custom-domain.bicep` at all just to learn them. Any of these work:

- `main.bicep`'s own deployment outputs (`fhirbridgeAppDomainVerificationId`, `fhirbridgeAppUrl`,
  etc.) from Step 1.
- `az containerapp show --name <prefix>-app --resource-group <rg> --query
  "{fqdn:properties.configuration.ingress.fqdn, verificationId:properties.customDomainVerificationId}"`
- `containerization/scripts/discover-custom-domains.ps1|sh` (prompts for prefix, lists all 3 apps).
- The Step 2 wizard itself shows both live per tab, before you even fill in a domain.

### Create DNS, then deploy once

1. At your DNS provider create, per app you want a domain on:
   - **CNAME** `your.domain` → the app's default FQDN
   - **TXT** `asuid.your.domain` → the app's verification ID
2. Wait for propagation (minutes to hours — check with `nslookup`/`dig`, or just try the deploy and
   let it tell you if it's not ready yet).
3. Open the Deploy-to-Azure link (`publish-deploy-artifacts.ps1`/`.sh` prints it), fill in
   `namePrefix` and whichever of the 3 tabs' domain fields you want, and deploy. One deploy
   registers the hostname, creates the certificate, and binds it for every domain you set.
4. Open `https://your.domain` and confirm the padlock, for each app.

If DNS was not actually ready, Azure rejects the deploy with `InvalidCustomHostNameValidation` (TXT
record not found) — wait and redeploy once it resolves; redeploying is always safe, including for a
domain that's already fully bound (idempotent no-op, confirmed live — the certificate is reused,
not recreated, and the app stays reachable throughout).

### CLI example

```bash
az deployment group create -g <rg> -f custom-domain.bicep \
  -p namePrefix=segue12 hapiTerminologyDomain=term.example.com
```

### Automating the DNS wait

`auto-bind-custom-domain.ps1`/`.sh` (in `containerization/scripts/`) does the manual "check DNS,
wait, then deploy" for you: reads each app's FQDN/verification ID directly (no deploy needed for
that), prints the DNS records, polls DNS itself until they resolve, then deploys
`custom-domain.bicep` once:

```powershell
.\containerization\scripts\auto-bind-custom-domain.ps1 -ResourceGroup rg-tusharpuri -NamePrefix segue12 `
  -HapiTerminologyDomain term.example.com
```

Drives `custom-domain.bicep` itself (not raw `az containerapp hostname` commands), so it can't hit
the IaC-drift problem Path B below has. Safe to re-run — a domain that's already bound is reported
and skipped without deploying anything.

### main.bicep's own domain parameters (fallback, not recommended)

`main.bicep` still has `fhirbridgeAppCustomDomain`/`demoAppCustomDomain`/
`hapiTerminologyCustomDomain`/`bindCustomDomainCertificates` — same 3-deploy flow, but every
redeploy resubmits the *entire* stack's parameters (all passwords, image tag, etc.), so a stale or
mistyped value elsewhere in that parameter set can drift other resources. Prefer Path A above;
use this only if you're already re-deploying `main.bicep` for another reason anyway and want to
fold the domain change into the same deploy.

## Path B — Operator script (any already-deployed app)

```powershell
cd containerization/scripts
.\manage-custom-domain.ps1 -ResourceGroup <rg> -AppName <prefix>-app `
  -EnvironmentName <prefix>-env -Domain app.example.com -Action Info
# create DNS, then:
.\manage-custom-domain.ps1 ... -Action Both -ValidationMethod CNAME
```

Bash: `manage-custom-domain.sh` with the same actions.

**IaC drift:** if you bind with the script, keep the matching domain (+ bind flag) in Bicep/TF
parameters on later applies or the next deploy may remove the hostname.

## Path C — Terraform (internal)

Same two-phase flags in `terraform/environments/azure`:

- `fhirbridge_app_custom_domain` / `demo_app_custom_domain`
- `bind_custom_domain_certificates = false` then `true` after DNS

## Marketplace / Partner Center (org process — not code)

After this domain flow is validated live:

1. Partner Center publisher account
2. CreateUiDefinition sandbox validation
3. Confirm vendor ACR access model (token-based is what production uses today)
4. Preview/test tenant dry-run
5. Microsoft certification submit

## Do not

- Deploy Path A (`custom-domain.bicep` / the Step 2 wizard) before DNS (CNAME + `asuid` TXT) has
  actually propagated — it fails cleanly with `InvalidCustomHostNameValidation`, but there's no way
  to skip that wait
- Test in subscriptions other than the agreed Ragu / Sponsorship test directory
- Leave custom domains out of template params after binding them with the script (Path B) or
  Terraform (Path C)
