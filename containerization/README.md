# FHIRBridge containerization

Packages FHIRBridge into 5 containers and deploys them via Terraform to local Docker, Azure, or
AWS. Everything here is additive: it doesn't touch the repo-root `docker-compose.yml` (a separate
dev/E2E dependency stack) or the existing Windows Service / GitHub Actions deploy path documented
in `../deploy/windows/README.md` — this is a new, parallel deployment path.

## Topology

| # | Container | Runs | Public? |
|---|---|---|---|
| 1 | `fhirbridge-app` | `FHIRBridge.Api` (loopback-only) + `FHIRBridge.Gateway` (serves the Angular portal, proxies `/api/**` and `/swagger/**` to the Api) | Yes — port 80 |
| 2 | `demo-app` | `HealthAppBackend`, self-hosting its own Angular build | Yes — port 5500 |
| 3 | `sqlserver` | `mcr.microsoft.com/mssql/server:2022-latest`, `MSSQL_PID=Express` — hosts both the `FHIRBridge` and `HealthAppDb` databases | No |
| 4 | `redis` | `redis:7-alpine` — backs `ConnectionStrings:Redis` for the Api + Worker | No |
| 5 | `worker` | `FHIRBridge.Worker` — background scheduler/pipeline processor, no HTTP endpoint | No |

`fhirbridge-app` bundles two processes in one image instead of one, because the existing Windows
deploy topology already establishes this exact split (Api never exposed, Gateway is the only public
entry point) — see `../deploy/windows/README.md`. No nginx or new reverse proxy was introduced;
`FHIRBridge.Gateway` already does this job. Both apps also already auto-provision their own schema
on startup (`Database.Migrate()` / `EnsureCreated()`), so `sqlserver` needs no init scripts.

## Folder layout

```
docker/            Dockerfiles + fhirbridge-app's entrypoint.sh
compose/            docker-compose.yml + .env.example — fast local path
scripts/            build-images.sh / .ps1 — builds & (optionally) pushes the 3 custom images
terraform/
  environments/
    local/          kreuzwerker/docker — same 5-container topology via `terraform apply`
    azure/          azurerm — Container Apps Environment, ACR, Storage, 5 Container Apps
    aws/             aws — VPC, ECR, ECS Fargate cluster, Cloud Map, EFS, ALB, 5 services
```

## 1. Build the images

All 3 Dockerfiles use the **repo root** as build context (they need `src/`, `portal/`,
`Demo_TestApp/`). Always build via the script rather than a bare `docker build` unless you also
pass `-f .../Dockerfile <repo-root>` yourself:

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

`fhirbridge-app` → http://localhost:8080, `demo-app` → http://localhost:5500. Or the Terraform
path (reproduces the same 5 containers, useful for testing the Terraform config itself):

```bash
cd terraform/environments/local
cp terraform.tfvars.example terraform.tfvars   # edit secrets
terraform init
terraform apply
```

## 3. Deploy to Azure or AWS

Both cloud environments assume the custom images already exist in **that cloud's own registry**
before `terraform apply` creates the compute that references them — Terraform here only deploys,
it never builds or pushes images (see each environment's `main.tf` header comment for the exact
bootstrap order: create the registry first with a targeted apply, push images, then a full apply).

```bash
cd terraform/environments/azure   # or .../aws
cp terraform.tfvars.example terraform.tfvars   # edit secrets
terraform init
terraform apply -target=azurerm_container_registry.acr   # or -target=aws_ecr_repository.<x> x3 on AWS
../../../scripts/build-images.sh -r <registry output from the targeted apply> -t <image_tag> -p
terraform apply
```

Outputs (`terraform output`) give you `fhirbridge_app_url` / `demo_app_url` (and, on AWS,
`ecr_repository_urls`) once the apply completes.

## Notes and known limitations

- **SQL Server Express + Redis are containerized in every environment** (including Azure/AWS),
  per an explicit choice to match the product topology exactly rather than swap in managed
  services. They're pinned to a single replica/task each — their data directories (Azure Files /
  EFS) aren't safe for concurrent multi-instance access.
- **Messaging is `InMemory`** in all 3 environments to keep the container count at exactly 5 —
  adding a RabbitMQ/Azure Service Bus/SQS container or managed queue later just means changing
  `Messaging__Provider` and adding one more container/service definition.
- **Plain HTTP, no TLS automation** — this matches the project's own current stance (see
  `../deploy/windows/README.md`: "TLS termination is a deliberate later step, not yet configured").
  Terminate TLS at the Container Apps / ALB layer (both support it) when you're ready.
- **Secrets** are passed as Terraform variables (`sensitive = true`) sourced from a gitignored
  `terraform.tfvars` / `.env`. For real production use, consider wiring Azure Key Vault / AWS
  Secrets Manager references directly into `terraform.tfvars` retrieval instead of plain local
  values — not done here to keep the first cut's scope matched to what was asked.
- `terraform validate` was run against all 3 environments; a real `terraform apply` against Azure
  or AWS needs your own cloud credentials/subscription and creates real, billable resources — run
  it yourself when you're ready (or ask me to walk through it with you).
