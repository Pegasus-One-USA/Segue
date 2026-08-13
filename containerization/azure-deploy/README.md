# FHIRBridge — easy-install Azure deployment (Bicep)

A single-template counterpart to [`../terraform/environments/azure`](../terraform/environments/azure):
same 5-container topology (`fhirbridge-app`, `demo-app`, `sqlserver`, `redis`, `worker`), same
Container Apps design, but expressed as **one Bicep file** so a customer can stand it up with a
single command or a single button click in their own Azure Portal, instead of running
`terraform init/apply`. Use whichever of the two fits the audience — this one for "install it like
a product," the Terraform one for your own CI/CD or repeatable internal environments.

## The one thing that can't be skipped: publish the images first

A one-click experience is only possible if the 3 custom images
(`fhirbridge-app`, `demo-app`, `fhirbridge-worker`) **already exist in a registry the customer's
Container Apps can reach** before they click deploy — there's no version of "single button" where
the customer also builds Docker images. That's a normal part of shipping software as a product: you
(the vendor) build and publish a release once; every customer's one-click deploy just references
it.

Use `../terraform/vendor-registry` for this — a persistent ACR in your own subscription, built for
exactly this purpose (see the containerization guide's "Vendor Registry" section for the full
walkthrough):

```bash
cd ../terraform/vendor-registry && terraform apply   # one-time setup
az acr login --name <acr_name output>
cd ../../scripts
./build-images.sh -r <acr_login_server output> -t v1.2.0 -p
# or on Windows: ./build-images.ps1 -Registry <acr_login_server output> -Tag v1.2.0 -Push
```

Docker builds locally here — source never leaves your machine, only the compiled image gets
pushed.

### Wiring up registry access (`createUiDefinition.json`) — do this before hosting the file

`createUiDefinition.json` no longer asks the customer for a registry at all — the "Container
Images" step only has a version field now. Registry access is fixed, not customer-editable, using
a **read-only ACR token** on the vendor registry:

```bash
az acr token create --registry <vendor acr_name> --name one-click-pull --scope-map _repositories_pull
```

This prints a username + password that can only pull images, nothing else, and can be individually
revoked at any time without affecting anything else (`az acr token delete --registry <acr_name>
--name one-click-pull`, then create a new one). Replace the 3 placeholder strings in
`createUiDefinition.json`'s `outputs` section with the real values before hosting it:

| Placeholder | Replace with |
|---|---|
| `__VENDOR_ACR_LOGIN_SERVER__` | The vendor registry's login server (`terraform output acr_login_server` in `../terraform/vendor-registry`) |
| `__VENDOR_ACR_PULL_TOKEN_USERNAME__` | The token name from the command above (`one-click-pull`) |
| `__VENDOR_ACR_PULL_TOKEN_PASSWORD__` | The password the `az acr token create` command printed |

**Trade-off worth knowing:** since `createUiDefinition.json` is hosted in a *publicly-readable*
storage container (Tier 2 below) so the Deploy-to-Azure button can fetch it, embedding this token
here means it's technically public too — anyone who downloads the JSON directly could read it out,
not just people who click the button. The bounded mitigation is exactly why a read-only, scoped,
revocable token is used here instead of an admin credential or a broader role: the worst case is
someone can pull your images, and you can kill that specific token at any time. If that trade-off
isn't acceptable for a given release, fall back to a public registry instead — no rework needed,
`main.bicep` already treats `imageRegistryUsername`/`imageRegistryPassword` as optional (empty
string skips registry auth entirely). Just set `__VENDOR_ACR_LOGIN_SERVER__` to the public
registry's address and leave the other two placeholders as empty strings (`""`) instead of real
credentials — the images become publicly downloadable in that case, so decide if that's acceptable
for this product first.

## Tier 1 — one command, works today

No hosting, no Marketplace account, nothing to publish anywhere except the images themselves:

```bash
az login
az group create --name fhirbridge-rg --location eastus

az deployment group create \
  --resource-group fhirbridge-rg \
  --template-file main.bicep \
  --parameters main.parameters.example.json \
  --parameters sqlSaPassword='<real password>' jwtSigningKey='<real 32+ char key>'
```

(Copy `main.parameters.example.json` and fill in your registry details first, or override every
value inline with `--parameters` as shown.) When it finishes:

```bash
az deployment group show --resource-group fhirbridge-rg --name main --query properties.outputs
```

gives you `fhirbridgeAppUrl` and `demoAppUrl`.

## Tier 2 — an actual "Deploy to Azure" button

The classic button (the same pattern GitHub-hosted OSS projects use) needs the **compiled ARM JSON**
reachable at a public HTTPS URL — the Portal's custom-deployment blade doesn't fetch `.bicep`
source directly.

```bash
az bicep build --file main.bicep --outfile main.json
```

Host `main.json` wherever you already publish things (a public GitHub repo's raw URL, a storage
account blob with public/SAS read access, your own website). Then the button markdown is:

```markdown
[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/<url-encoded-main.json-URL>)
```

Clicking it opens the customer's own Azure Portal with a "Custom deployment" form auto-generated
from `main.json`'s parameters (secure parameters like `sqlSaPassword` automatically render as
password fields) — no createUiDefinition wiring required for this path.

## Tier 3 — a native, nicely-labeled wizard (Template Specs or Marketplace)

`createUiDefinition.json` in this folder is a ready-to-use custom wizard (grouped steps: FHIRBridge
Settings, Container Images) that upgrades the plain auto-generated form into labeled sections with
tooltips and conditional fields (registry credentials only appear if you pick "Private"). It isn't
consumed by the plain Tier 2 button — it's meant for either of these two, both of which give the
customer a genuinely native "Deploy" experience inside their own Portal:

- **Azure Template Specs** (`Microsoft.Resources/templateSpecs`) — publish `main.bicep` + this UI
  definition as a versioned, shareable resource (via RBAC, no Marketplace account needed); the
  customer sees it in their own Portal under Template Specs and deploys with the custom wizard.
- **Azure Marketplace Managed Application** — the full "product" listing (`Get It Now` button,
  discoverable in Marketplace search) via Partner Center, using this same template + UI definition
  packaged into a `.zip`. This path additionally requires a Partner Center publisher account and
  Microsoft's certification review before it's public.

## Notes and known limitations (same as the Terraform Azure environment)

- **SQL Server Express + Redis are containerized**, pinned to a single replica each — their Azure
  Files-backed data directories aren't safe for concurrent multi-instance access.
- **`fhirbridge-app` and `worker` both auto-migrate `FHIRBridgeDb`** on first boot and can race on
  the initial `CREATE DATABASE` on a brand-new database; Container Apps replaces crashed replicas
  automatically, turning a lost race into a self-healing retry. See the containerization guide
  (`Documents/Containerization-Multi-Cloud-Guide.html`) for the full writeup.
- **Messaging is `InMemory`** — no queue container in this topology.
- **Custom domains + managed SSL** — supported via a required two-phase flow (`bindCustomDomainCertificates`):
  Phase 1 registers hostnames (`bindingType: Disabled`); after DNS CNAME + asuid TXT, Phase 2 binds
  free managed certificates (`SniEnabled`). Checking Bind SSL before the hostname exists causes
  `RequireCustomHostnameInEnvironment`. Operator fallback: `../scripts/manage-custom-domain.ps1|.sh`.
- **Secrets are plain Bicep `@secure()` parameters**, not Key Vault references — fine for a
  customer-run one-click deploy, but consider Key Vault integration if you want secret rotation
  without a redeploy.
- Validate Phase 1 then Phase 2 against a real subscription before Marketplace certification.
  Live test evidence: an earlier single-shot domain+cert deploy failed with
  `RequireCustomHostnameInEnvironment` — this two-phase flag is the fix.
