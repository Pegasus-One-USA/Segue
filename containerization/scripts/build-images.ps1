# Builds (and optionally pushes) the 4 custom FHIRBridge container images from the repo root -- so
# one command refreshes everything the Container Apps environment actually deploys.
# Terraform's azure/aws environments assume this has already been run against their registry.
# fhirbridge-redis (containerization/docker/redis-tls) is stock redis:7-alpine plus a fixed,
# committed self-signed TLS certificate — see that Dockerfile's own comment for why it's a custom
# image at all (FHIRBridge.Api/.Worker refuse a plaintext Redis connection outside Development).
# fhirbridge-postgres (containerization/docker/postgres-local) is stock postgres:16-alpine plus a
# custom entrypoint that keeps PGDATA on local ephemeral disk and treats a mounted Azure Files
# share purely as a backup target — see that Dockerfile's own comment for why (Postgres's startup
# permission check can never pass directly on Azure Files/SMB).
#
# Usage:
#   ./build-images.ps1                                          # local tags only, no push
#   ./build-images.ps1 -Tag v1.2.0                               # local tags with a specific version
#   ./build-images.ps1 -Registry myregistry.azurecr.io -Tag v1.2.0 -Push   # build+push all 4, ACR
#   ./build-images.ps1 -Registry 123456789012.dkr.ecr.us-east-1.amazonaws.com/fhirbridge -Tag v1.2.0 -Push  # ECR, all 4
#
# -Registry               registry/repo prefix images are tagged with (default: none — local tag only)
# -Tag                    image tag (default: local)
# -Push                   push each image after building (requires -Registry and that you're already
#                         logged in to the registry, e.g. `az acr login` / `aws ecr get-login-password | docker login`)
param(
    [string]$Registry = "",
    [string]$Tag = "local",
    [switch]$Push
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$Prefix = if ($Registry -ne "") { "$($Registry.TrimEnd('/'))/" } else { "" }

$Images = @{
    "fhirbridge-app"      = "containerization/docker/fhirbridge-app/Dockerfile"
    "fhirbridge-worker"   = "containerization/docker/worker/Dockerfile"
    "fhirbridge-redis"    = "containerization/docker/redis-tls/Dockerfile"
    "fhirbridge-postgres" = "containerization/docker/postgres-local/Dockerfile"
}
# fhirbridge-app bakes $Tag into its Angular build (footer version display) -
# fhirbridge-worker has no UI, so it doesn't take this build-arg.
$UiImages = @("fhirbridge-app")

foreach ($name in $Images.Keys) {
    $dockerfile = Join-Path $RepoRoot $Images[$name]
    $fullTag = "$Prefix$name`:$Tag"
    $buildArgs = if ($UiImages -contains $name) { @("--build-arg", "APP_VERSION=$Tag") } else { @() }

    Write-Host "==> Building $fullTag ($($Images[$name]))"
    docker build -f $dockerfile -t $fullTag @buildArgs $RepoRoot
    if ($LASTEXITCODE -ne 0) { throw "docker build failed for $name" }

    if ($Push) {
        if ($Registry -eq "") {
            throw "-Push requires -Registry <registry>"
        }
        Write-Host "==> Pushing $fullTag"
        docker push $fullTag
        if ($LASTEXITCODE -ne 0) { throw "docker push failed for $name" }
    }
}

Write-Host "Done. Images tagged with prefix '$Prefix' and tag '$Tag'."
