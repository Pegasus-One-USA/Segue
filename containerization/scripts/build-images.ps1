# Builds (and optionally pushes) the 3 custom FHIRBridge container images from the repo root.
# Terraform's azure/aws environments assume this has already been run against their registry.
#
# Usage:
#   ./build-images.ps1                                          # local tags only, no push
#   ./build-images.ps1 -Tag v1.2.0                               # local tags with a specific version
#   ./build-images.ps1 -Registry myregistry.azurecr.io -Tag v1.2.0 -Push   # build, tag, push to ACR
#   ./build-images.ps1 -Registry 123456789012.dkr.ecr.us-east-1.amazonaws.com/fhirbridge -Tag v1.2.0 -Push  # ECR
#
# -Registry  registry/repo prefix images are tagged with (default: none — local tag only)
# -Tag       image tag (default: local)
# -Push      push each image after building (requires -Registry and that you're already logged
#            in to the registry, e.g. `az acr login` / `aws ecr get-login-password | docker login`)
param(
    [string]$Registry = "",
    [string]$Tag = "local",
    [switch]$Push
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$Prefix = if ($Registry -ne "") { "$($Registry.TrimEnd('/'))/" } else { "" }

$Images = @{
    "fhirbridge-app"    = "containerization/docker/fhirbridge-app/Dockerfile"
    "demo-app"          = "containerization/docker/demo-app/Dockerfile"
    "fhirbridge-worker" = "containerization/docker/worker/Dockerfile"
}

foreach ($name in $Images.Keys) {
    $dockerfile = Join-Path $RepoRoot $Images[$name]
    $fullTag = "$Prefix$name`:$Tag"

    Write-Host "==> Building $fullTag ($($Images[$name]))"
    docker build -f $dockerfile -t $fullTag $RepoRoot
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
