# FHIRBridge containerization

Packages FHIRBridge into 4 containers and deploys them via Terraform to local Docker, Azure, or
AWS. Everything here is additive: it doesn't touch the repo-root `docker-compose.yml` (a separate
dev/E2E dependency stack) or the existing Windows Service / GitHub Actions deploy path documented
in `../deploy/windows/README.md` — this is a new, parallel deployment path.

## Topology

| # | Container | Runs | Public? |
|---|---|---|---|
| 1 | `fhirbridge-app` | `FHIRBridge.Api` (loopback-only) + `FHIRBridge.Gateway` (serves the Angular portal, proxies `/api/**` and `/swagger/**` to the Api) | Yes — port 80 |
| 2 | `postgres` | `postgres:16-alpine` — hosts the `FHIRBridge` database | No |
| 3 | `redis` | `redis:7-alpine` — backs `ConnectionStrings:Redis` for the Api + Worker | No |
| 4 | `worker` | `FHIRBridge.Worker` — background scheduler/pipeline processor, no HTTP endpoint | No |

Each environment can swap `postgres`/`redis` for a managed PaaS equivalent instead (Azure Database
for PostgreSQL / Azure Cache for Redis on Azure, Amazon RDS for PostgreSQL on AWS) via a boolean
toggle in that environment's `terraform.tfvars` — see that environment's `variables.tf` for the
exact flag names. Neither is available for the local Docker environment (no cloud to provision a
managed service in), which always uses the containerized pair.

`fhirbridge-app` bundles two processes in one image instead of one, because the existing Windows
deploy topology already establishes this exact split (Api never exposed, Gateway is the only public
entry point) — see `../deploy/windows/README.md`. No nginx or new reverse proxy was introduced;
`FHIRBridge.Gateway` already does this job. The app already auto-provisions its own schema on
startup (`Database.Migrate()`), so `postgres` needs no init scripts.

## Folder layout

```
docker/            Dockerfiles + fhirbridge-app's entrypoint.sh
compose/            docker-compose.yml + .env.example — fast local path
scripts/            build-images.sh / .ps1 — builds & (optionally) pushes the 3 custom images
                    manage-custom-domain.sh / .ps1 — Info/Wait/Add/Bind custom domain + managed SSL
                    cleanup-*.sh / .ps1 — tear down each environment below
terraform/
  environments/
    local/          kreuzwerker/docker — same 4-container topology via `terraform apply`
    azure/          azurerm — Container Apps Environment, ACR, Storage, 4 Container Apps
                    (custom domains: bind_custom_domain_certificates two-phase flag)
    aws/             aws — VPC, ECR, ECS Fargate cluster, Cloud Map, EFS, ALB, 4 services
  vendor-registry/  a SEPARATE, persistent ACR for publishing/versioning release images — not a
                    deployment target, see section 4 below
azure-deploy/       Bicep one-click / Marketplace path — see azure-deploy/CUSTOM_DOMAIN_SELF_SERVICE.md
```

## 1. Build the images

All 3 Dockerfiles use the **repo root** as build context (they need `src/`, `portal/`). Always
build via the script rather than a bare `docker build` unless you also pass
`-f .../Dockerfile <repo-root>` yourself:

```bash
# Local only, no registry:
./scripts/build-images.sh
# or on Windows:
./scripts/build-images.ps1
```

Pass `-r <registry>` (`-Registry` on Windows) and `-p`/`-Push` to build for and push to a real
registry — see the script's header comment for Azure ACR / AWS ECR examples.

## 2. Run locally

Either the fast manual path:

```bash
cd compose
cp .env.example .env   # edit secrets
docker compose up -d
```

`fhirbridge-app` → http://localhost:8080. Or the Terraform path (reproduces the same 4 containers,
useful for testing the Terraform config itself):

```bash
cd terraform/environments/local
cp terraform.tfvars.example terraform.tfvars   # edit secrets
terraform init
terraform apply
```

## 3. Deploy to Azure or AWS

Both cloud environments assume the custom images already exist in **that cloud's own registry**
before `terraform apply` creates the compute that references them — Terraform here only deploys,
it never builds or pushes images.

```bash
cd terraform/environments/azure   # or .../aws
cp terraform.tfvars.example terraform.tfvars   # edit secrets
terraform init
terraform apply -target=azurerm_container_registry.acr   # or -target=aws_ecr_repository.<x> x3 on AWS
```

**Getting images into that registry — two paths, pick based on whether section 4's vendor registry
already has a published version:**

- **Recommended (Azure): import an already-published version, no local Docker needed for this
  step** — see section 4 below. `az acr import --name <this ACR's name> --source
  <vendor_acr_login_server>/fhirbridge-app:v1.2.0 --image fhirbridge-app:v1.2.0` (repeat for
  `fhirbridge-worker`/`fhirbridge-redis`), then set `image_tag = "v1.2.0"` in `terraform.tfvars`.
- **Direct build (works today for both Azure and AWS, no vendor registry required)** — build with
  local Docker straight into this deployment's own registry:
  `../../../scripts/build-images.sh -r <registry output from the targeted apply> -t <image_tag> -p`.
  This is the only option for AWS right now — ECR has no built-in equivalent to `az acr import` for
  a registry-to-registry copy without extra tooling (e.g. `skopeo`/`crane`), so publishing a
  version once and reusing it across deployments (section 4) is currently an Azure-only workflow.

```bash
terraform apply
```

Outputs (`terraform output`) give you `fhirbridge_app_url` (and, on AWS, `ecr_repository_urls`)
once the apply completes.

## 4. Publish a versioned release, then deploy it to a client

`terraform/vendor-registry/` creates one persistent, vendor-owned Azure Container Registry —
deliberately separate from every deployment environment above, since it's meant to outlive any
number of those being torn down and recreated during testing, and holds real release history
instead of disposable test images.

**Set it up once:**

```bash
az group create --name rg-fhirbridge-vendor --location eastus   # or reuse an existing group you have access to
cd terraform/vendor-registry
cp terraform.tfvars.example terraform.tfvars   # set resource_group_name to whichever group you're using
terraform init
terraform apply
terraform output acr_login_server
```

**Publish a version — deliberately built with local Docker, not a remote/cloud build service:**

```bash
az acr login --name <acr_name output>
../../scripts/build-images.sh -r <acr_login_server output> -t v1.2.0 -p   # or build-images.ps1 -Registry ... -Tag ... -Push
```

This is the same script as section 1 — `docker build` runs on your own machine, so your source
never leaves it; only the compiled image gets pushed. (Azure's remote-build option, `az acr
build`, would instead upload your full source tree to a cloud build sandbox — deliberately not
used here for that reason.) Every version tag stays in the registry permanently; re-running this
with a tag that already exists overwrites it, so use a new tag for each real release.

**Deploy a specific version to a client's own environment** — copy the already-built image into
*their* registry (a server-to-server copy, no rebuild, no source involved either way), then point
their deployment at it:

```bash
az acr import --name <client-environment's-acr-name> --source <vendor acr_login_server>/fhirbridge-app:v1.2.0 --image fhirbridge-app:v1.2.0
# repeat for fhirbridge-worker and fhirbridge-redis
```

Set `image_tag = "v1.2.0"` in that client's `terraform.tfvars` (or `imageTag` for the Bicep path)
and apply as usual. Tear the vendor registry down (rarely needed — it's meant to persist) with
`../scripts/cleanup-vendor-registry.sh` / `.ps1`.

## Notes and known limitations

- **Postgres + Redis are containerized by default in every environment** (including Azure/AWS),
  per an explicit choice to match the product topology exactly rather than always require a
  managed service. Each can be swapped for a managed PaaS equivalent instead via a per-environment
  boolean toggle (see the Topology section above) — the local environment has no such toggle, since
  there's no cloud to provision a managed service in. Whichever containerized option stays in use
  is pinned to a single replica/task — its data directory (Azure Files / EFS) isn't safe for
  concurrent multi-instance access.
- **Messaging is `InMemory`** in all 3 environments to keep the container count at exactly 4 —
  adding a RabbitMQ/Azure Service Bus/SQS container or managed queue later just means changing
  `Messaging__Provider` and adding one more container/service definition.
- **TLS**: Azure Container Apps' public ingress (`fhirbridge-app`) gets automatic, Microsoft-managed
  TLS on its `*.azurecontainerapps.io` domain — no extra config needed. AWS's ALB terminates TLS
  using a **self-signed certificate** generated by this config (`main.tf`'s `tls_private_key`/
  `tls_self_signed_cert`/`aws_acm_certificate` resources) — browsers will show a trust warning until
  it's swapped for a certificate from a real CA (import it into `aws_acm_certificate` or request
  one via `aws_acm_certificate` + DNS validation instead). Local Docker Compose/Terraform stays
  plain HTTP — it's a dev-only loopback setup, never exposed past `localhost`.
- **Secrets**: AWS reads `postgres_password`/`jwt_signing_key`/`redis_password` out of AWS Secrets
  Manager (the ECS task definitions' `secrets` blocks in `tasks.tf`, never plaintext
  `environment`) — `postgres_password` only when the containerized Postgres path is in use; the
  managed RDS path holds its own master password on the `aws_db_instance` resource directly instead.
  Azure seeds the containerized path's secrets into a Key Vault the same way (Access Policy
  authorization, not RBAC — RBAC role assignments need Owner/User Access Administrator, a narrower
  permission than many Contributor-scoped accounts have) on first apply, and every Container App
  reads the Key Vault secret's value from then on — rotate via `az keyvault secret set` (or the
  Portal) and it sticks, since `lifecycle.ignore_changes` stops a later `terraform apply` from
  overwriting a rotated value with the old `terraform.tfvars` one. The local environment has no
  cloud secret store to wire up, so it stays as plain (`sensitive = true`) Terraform variables
  sourced from a gitignored `terraform.tfvars`/`.env`.
- **Network isolation**: AWS's ECS tasks all run in private subnets with no public IP — only the
  ALB is internet-facing, and outbound internet access (ECR pulls, CloudWatch, etc.) goes through a
  single NAT Gateway (an hourly + per-GB cost — see the cost inventory doc). Azure's containerized
  `postgres`/`redis` Container Apps already use internal-only ingress (unreachable from outside the
  environment) with no separate subnet config needed; their managed-service alternatives (Azure
  Database for PostgreSQL, Azure Cache for Redis) use their own firewall-rule-based access control
  instead, since this deployment has no private VNet for them to join.
- `terraform validate` was run against all 3 environments after every change in this doc; a real
  `terraform apply` against Azure or AWS needs your own cloud credentials/subscription and creates
  real, billable resources (including the Key Vault and NAT Gateway added above) — run it yourself
  when you're ready (or ask me to walk through it with you).
