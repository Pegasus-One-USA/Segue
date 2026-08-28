# Publishes a build from the current branch to the vendor registry (seguebuilds - renamed from the
# original auto-generated fhirbridgevendor8ae7f3; see
# Documents/Vendor-Registry-Cleanup-And-Rename-Log.md), then deploys it via the Bicep one-click
# template, including the HAPI terminology server. Run from anywhere - every path below is
# absolute. Requires Docker Desktop running and `az login` already done.
#
# Usage:
#   ./deploy-vendor.ps1                    # tag defaults to v1.1.0
#   ./deploy-vendor.ps1 -Tag v1.2.0        # any tag - if it already exists in the registry, the
#                                           # push overwrites it (a normal docker/ACR push always
#                                           # re-points an existing tag at the new build; this is
#                                           # not a special mode, just what pushing to a tag does).
#                                           # Use a NEW tag instead if you want the old version to
#                                           # stay pullable side by side with this one.
#   ./deploy-vendor.ps1 -Tag v1.2.0 -ResourceGroup another-rg
#   ./deploy-vendor.ps1 -PublishOnly       # build + push only, skips the deploy step entirely -
#                                           # no app secrets (sqlSaPassword etc.) are needed or
#                                           # asked for in this mode, since those only configure the
#                                           # DEPLOYED containers, not the image publish step.
param(
    [string]$Tag = "v1.1.0",
    [string]$ResourceGroup = "rg-tusharpuri",
    [switch]$PublishOnly
)

$ErrorActionPreference = "Stop"
$RepoRoot = "D:\Project\FHIRBridge\FHIRBridge2\FHIRBridge"
$AcrName = "seguebuilds"
$AcrLoginServer = "$AcrName.azurecr.io"
$AzureDeployDir = "$RepoRoot\containerization\azure-deploy"

Write-Host "==> Logging in to $AcrLoginServer"
az acr login --name $AcrName
if ($LASTEXITCODE -ne 0) { throw "az acr login failed - is Docker Desktop running?" }

# Informational only - the push below overwrites regardless, and a brand-new/never-pushed-to
# registry legitimately has no repositories yet ("repository ... is not found" from az is expected
# in that case, not a real error) - ErrorAction SilentlyContinue plus try/catch covers both how az
# CLI failures can surface in PowerShell (non-zero exit code, and/or a terminating NativeCommandError
# when $ErrorActionPreference = "Stop" is in effect, as it is for this whole script).
$Images = @("fhirbridge-app", "demo-app", "fhirbridge-worker")
foreach ($image in $Images) {
    try {
        $existing = az acr repository show-tags --name $AcrName --repository $image --query "[?@=='$Tag']" -o tsv --only-show-errors 2>$null
    } catch {
        $existing = $null
    }
    if ($existing) {
        Write-Host "NOTE: ${image}:${Tag} already exists in $AcrName - this run will overwrite it."
    }
}

Write-Host "==> Building and pushing all 3 images, tag $Tag"
& "$RepoRoot\containerization\scripts\build-images.ps1" -Registry $AcrLoginServer -Tag $Tag -Push
if ($LASTEXITCODE -ne 0) { throw "build-images.ps1 failed" }

if ($PublishOnly) {
    Write-Host "==> -PublishOnly: images published, skipping deploy. Run without -PublishOnly (or use az deployment group create / az containerapp update yourself) when you're ready to deploy this tag."
    exit 0
}

Write-Host "==> Deploying via Bicep to resource group $ResourceGroup"
# Fill in real values for the 4 password/key parameters below before running (or edit this file).
az deployment group create `
  --resource-group $ResourceGroup `
  --template-file "$AzureDeployDir\main.bicep" `
  --parameters "$AzureDeployDir\main.parameters.example.json" `
  --parameters `
    imageRegistryServer=$AcrLoginServer `
    imageTag=$Tag `
    sqlSaPassword='CHANGE-ME-Str0ng!' `
    jwtSigningKey='CHANGE-ME-replace-with-a-long-random-string-32-chars-min' `
    redisPassword='CHANGE-ME-strong-redis-password' `
    hapiTerminologyPostgresPassword='CHANGE-ME-strong-postgres-password'
if ($LASTEXITCODE -ne 0) { throw "az deployment group create failed" }

Write-Host ""
Write-Host "==> Deployment outputs (URLs):"
az deployment group show --resource-group $ResourceGroup --name main --query properties.outputs
