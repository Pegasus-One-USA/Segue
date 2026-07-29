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

```bash
# From the repo root, once per release:
../scripts/build-images.sh -r <your-registry> -t v1.2.0 -p
# or on Windows:
../scripts/build-images.ps1 -Registry <your-registry> -Tag v1.2.0 -Push
```

`<your-registry>` can be:
- **A private registry you control** (an Azure Container Registry in your own subscription, a
  private GHCR/Docker Hub repo). Customers then need pull credentials — `imageRegistryUsername`/
  `imageRegistryPassword` in this template — which you provide them (e.g. an ACR token scoped to
  read-only pull).
- **A public registry** (a public GHCR package, public Docker Hub repo). No credentials needed at
  all — leave `imageRegistryUsername`/`imageRegistryPassword` blank and Container Apps pulls
  anonymously. Simplest for the customer, but the images (compiled .NET DLLs, Angular bundles) are
  then publicly downloadable — decide if that's acceptable for this product before choosing this
  route.

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
- **Plain HTTPS via Container Apps' built-in ingress**, no custom domain/cert wiring here.
- **Secrets are plain Bicep `@secure()` parameters**, not Key Vault references — fine for a
  customer-run one-click deploy, but consider Key Vault integration if you want secret rotation
  without a redeploy.
- This template hasn't been run against a real subscription yet — `az deployment group validate`
  (or a real `create`) is worth doing before handing it to a client.
