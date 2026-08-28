# Segue / FHIRBridge — Custom domain + SSL self-service runbook

For clients and operators. Fixes the Marketplace/Deploy-to-Azure gap where a single-shot
domain+certificate deploy failed with `RequireCustomHostnameInEnvironment`.

## Why two phases?

Azure managed certificates require the **hostname to already exist** on a Container App in the
environment. Creating the cert first fails. Correct order:

1. Register hostname (`bindingType: Disabled`)
2. Create DNS (CNAME + `asuid` TXT)
3. Create managed cert + bind (`SniEnabled`)

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
no certificate checkbox or separate link at all. `custom-domain.bicep` auto-detects, per app, which
phase to run: if the domain you give isn't already in that app's `ingress.customDomains`, it
registers the hostname only; if it's already there (from an earlier run), it automatically creates
and binds the managed certificate instead. So the whole flow is just **run the same deploy twice**
— same name prefix, same domain(s) — with DNS propagation in between. The wizard has a name-prefix
field in Basics plus 3 tabs (one per app, `isWizard: true` so Basics must be completed before any
tab renders). Azure shows a one-time "Do you trust the authors code?" prompt the first time any
template using a live API lookup like this is opened — expected, not a bug, safe to accept.

Each tab shows, in order: the app's name; a raw JSON dump of the live app (one
`Microsoft.Solutions.ArmApiControl` GET per tab) with a note to look for `"fqdn"` (the CNAME target)
and `"customDomainVerificationId"` (the TXT value) inside it; the domain textbox; and, once you type
a domain, the exact CNAME/TXT record **names** to create (you pair those names with the values you
found in the raw dump above). It's a raw dump rather than neatly-parsed fields for a concrete
reason — see point 3 below.

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
   known workaround for extracting a *specific* nested field this way. The only reliable pattern
   found is `string(steps('step').controlName)` — the WHOLE control result, zero further chaining —
   which is why the current wizard shows a raw dump instead of parsed `fqdn`/`verificationId` values.
   If a cleaner display is ever wanted, revisit this constraint first (e.g. test whether a `variables`
   section or an intermediate `Microsoft.Common.TextBlock` reference changes what's honored) before
   attempting chained access again.
4. Bicep-only syntax sugar (the `x => expr` arrow-lambda short form, `.?prop ?? default`
   safe-navigation) does NOT survive into raw createUiDefinition JSON — that's evaluated by a
   separate, more limited engine, so lambdas must use the explicit
   `lambda('x', ...)`/`lambdaVariables('x')` form and optional-property access must use
   `coalesce(tryGet(obj, 'prop'), default)` (confirmed by inspecting what Bicep itself compiles that
   sugar down to). Moot for the current wizard (no chaining left to need this), but relevant if
   nested-field extraction is ever revisited per point 3.

### Run 1 — Register hostname(s)

1. Open the Deploy-to-Azure link (`publish-deploy-artifacts.ps1`/`.sh` prints it).
2. Fill in `namePrefix` (e.g. `segue12`), then whichever of the 3 tabs' domain fields you want —
   leave the rest blank to skip those apps this run.
3. In each tab's raw app-details dump, find `"fqdn"` and `"customDomainVerificationId"`.
4. At your DNS provider create, per app:
   - **CNAME** `your.domain` → the `fqdn` value
   - **TXT** `asuid.your.domain` → the `customDomainVerificationId` value
5. Deploy (this registers the hostname(s) only — no certificate yet, regardless of whether DNS is
   ready). Wait for DNS propagation (minutes to hours). Check CAA if certs fail later.

### Run 2 — Bind managed SSL

1. Open the **exact same** Deploy-to-Azure link again, with the **same** `namePrefix` and the
   **same** domain(s) in the same tabs.
2. Deploy — since each domain is now already registered on its app, `custom-domain.bicep`
   auto-detects this and creates + binds the managed certificate(s) instead.
3. Open `https://your.domain` and confirm the padlock, for each app.

If you deploy Run 2 before DNS has actually propagated, Azure rejects it with
`InvalidCustomHostNameValidation` (TXT record not found) — just wait and re-run once it resolves.

### CLI example (same command both times — set more domain params to do several apps at once)

```bash
# Run 1 - domain not yet registered, so this registers the hostname only
az deployment group create -g <rg> -f custom-domain.bicep \
  -p namePrefix=segue12 hapiTerminologyDomain=term.example.com

# After DNS is ready — Run 2, the EXACT same command: it detects the domain is already registered
# and automatically creates + binds the managed certificate instead
az deployment group create -g <rg> -f custom-domain.bicep \
  -p namePrefix=segue12 hapiTerminologyDomain=term.example.com
```

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

- Check “Bind SSL” on the first domain deploy for a brand-new hostname
- Test in subscriptions other than the agreed Ragu / Sponsorship test directory
- Leave custom domains out of template params after binding them with the script
