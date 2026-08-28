# Builds (and optionally pushes) the 4 custom FHIRBridge container images from the repo root, plus
# (Azure/ACR only, when -Push is set) imports hapi-terminology under the same tag -- so one command
# refreshes everything the Container Apps environment actually deploys.
# Terraform's azure/aws environments assume this has already been run against their registry.
# fhirbridge-redis (containerization/docker/redis-tls) is stock redis:7-alpine plus a fixed,
# committed self-signed TLS certificate — see that Dockerfile's own comment for why it's a custom
# image at all (FHIRBridge.Api/.Worker refuse a plaintext Redis connection outside Development).
# hapi-terminology is different from the other 4 -- it's a stock third-party image (hapiproject/hapi
# on Docker Hub), not built from a Dockerfile we own, so there's nothing to `docker build` for it.
# Against an ACR (-Registry ...azurecr.io) it's imported via `az acr import` (a server-to-server
# copy, no local Docker involved). Against any other registry (ECR included), it falls back to a
# local `docker pull` + `tag` + `push` -- slower (goes through local bandwidth/disk) but works
# anywhere.
#
# Usage:
#   ./build-images.ps1                                          # local tags only, no push
#   ./build-images.ps1 -Tag v1.2.0                               # local tags with a specific version
#   ./build-images.ps1 -Registry myregistry.azurecr.io -Tag v1.2.0 -Push   # build+push+import all 5, ACR (server-to-server)
#   ./build-images.ps1 -Registry 123456789012.dkr.ecr.us-east-1.amazonaws.com/fhirbridge -Tag v1.2.0 -Push  # ECR, all 5 (hapi-terminology via local pull/tag/push)
#
# -Registry               registry/repo prefix images are tagged with (default: none — local tag only)
# -Tag                    image tag (default: local)
# -Push                   push each image after building (requires -Registry and that you're already
#                         logged in to the registry, e.g. `az acr login` / `aws ecr get-login-password | docker login`)
# -HapiTerminologySource  upstream image to import as hapi-terminology (default: Docker Hub's :latest
#                         at the time this runs -- re-run to pick up a newer upstream release)
# -SkipHapiTerminology    don't import hapi-terminology even when -Push + an ACR -Registry are set
param(
    [string]$Registry = "",
    [string]$Tag = "local",
    [switch]$Push,
    [string]$HapiTerminologySource = "docker.io/hapiproject/hapi:latest",
    [switch]$SkipHapiTerminology
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$Prefix = if ($Registry -ne "") { "$($Registry.TrimEnd('/'))/" } else { "" }

$Images = @{
    "fhirbridge-app"    = "containerization/docker/fhirbridge-app/Dockerfile"
    "demo-app"          = "containerization/docker/demo-app/Dockerfile"
    "fhirbridge-worker" = "containerization/docker/worker/Dockerfile"
    "fhirbridge-redis"  = "containerization/docker/redis-tls/Dockerfile"
}
# fhirbridge-app and demo-app bake $Tag into their Angular build (footer version display) -
# fhirbridge-worker has no UI, so it doesn't take this build-arg.
$UiImages = @("fhirbridge-app", "demo-app")

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

if ($Push -and -not $SkipHapiTerminology) {
    if ($Registry -match '\.azurecr\.io$') {
        # Server-to-server copy (Azure's backend pulls hapiproject/hapi directly) - no local
        # download/re-upload of this (large) image needed, unlike the generic docker pull/tag/push
        # fallback below.
        $acrName = $Registry -replace '\.azurecr\.io$', ''
        if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
            Write-Warning "Skipping hapi-terminology import: 'az' CLI not found on PATH."
        } else {
            Write-Host "==> Importing hapi-terminology:$Tag from $HapiTerminologySource into $Registry (server-to-server, via az acr import)"
            az acr import --name $acrName --source $HapiTerminologySource --image "hapi-terminology:$Tag" --force
            if ($LASTEXITCODE -ne 0) { throw "az acr import failed for hapi-terminology" }
        }
    } else {
        # No ECR (or generic registry) equivalent to az acr import's server-to-server copy exists -
        # this has to go through local Docker: pull the upstream image, retag it, push it. Slower
        # and uses local bandwidth/disk, but works against any registry, not just ACR.
        Write-Host "==> Pulling $HapiTerminologySource (no server-to-server import available for '$Registry' - falling back to local pull/tag/push)"
        docker pull $HapiTerminologySource
        if ($LASTEXITCODE -ne 0) { throw "docker pull failed for $HapiTerminologySource" }
        $hapiFullTag = "$Prefix" + "hapi-terminology`:$Tag"
        docker tag $HapiTerminologySource $hapiFullTag
        if ($LASTEXITCODE -ne 0) { throw "docker tag failed for hapi-terminology" }
        Write-Host "==> Pushing $hapiFullTag"
        docker push $hapiFullTag
        if ($LASTEXITCODE -ne 0) { throw "docker push failed for hapi-terminology" }
    }
}

Write-Host "Done. Images tagged with prefix '$Prefix' and tag '$Tag'."
