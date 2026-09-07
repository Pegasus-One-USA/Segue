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

Write-Host "==> Ensuring the Redis TLS certificate exists (generates one on first run, reuses it otherwise)"
# -Quiet: Write-Host output can't be captured via a pipe/assignment anyway (it bypasses the
# success stream entirely) - -Quiet just also suppresses it from being double-printed to this
# console, since generate-cert.ps1's own "return $thumbprint" is the only thing actually captured.
$RedisThumbprint = & "$RepoRoot\containerization\docker\redis-tls\generate-cert.ps1" -Quiet
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RedisThumbprint)) {
    throw "generate-cert.ps1 did not return a certificate thumbprint."
}
Write-Host "    Using Redis certificate thumbprint: $RedisThumbprint"

Write-Host "==> Logging in to $AcrLoginServer"
az acr login --name $AcrName
if ($LASTEXITCODE -ne 0) { throw "az acr login failed - is Docker Desktop running?" }

# Informational only - the push below overwrites regardless, and a brand-new/never-pushed-to
# registry legitimately has no repositories yet ("repository ... is not found" from az is expected
# in that case, not a real error) - ErrorAction SilentlyContinue plus try/catch covers both how az
# CLI failures can surface in PowerShell (non-zero exit code, and/or a terminating NativeCommandError
# when $ErrorActionPreference = "Stop" is in effect, as it is for this whole script).
$Images = @("fhirbridge-app", "demo-app", "fhirbridge-worker", "fhirbridge-redis", "fhirbridge-postgres")
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

Write-Host "==> Building and pushing all custom images, tag $Tag"
& "$RepoRoot\containerization\scripts\build-images.ps1" -Registry $AcrLoginServer -Tag $Tag -Push
if ($LASTEXITCODE -ne 0) { throw "build-images.ps1 failed" }

if ($PublishOnly) {
    Write-Host "==> -PublishOnly: images published, skipping deploy. Run without -PublishOnly (or use az deployment group create / az containerapp update yourself) when you're ready to deploy this tag."
    exit 0
}

Write-Host "==> Deploying via Bicep to resource group $ResourceGroup"
# seguebuilds has neither the admin user nor anonymous pull enabled, so Container Apps needs real
# registry credentials to pull - same read-only, scoped "one-click-pull" ACR token already embedded
# in createUiDefinition.json for the customer-facing wizard (see README.md's "Wiring up registry
# access" section). Without these two parameters the deploy succeeds but every Container App fails
# to start with "UNAUTHORIZED: authentication required" pulling from seguebuilds.azurecr.io.
# Fill in real values for the 4 password/key parameters below before running (or edit this file).
az deployment group create `
  --resource-group $ResourceGroup `
  --template-file "$AzureDeployDir\main.bicep" `
  --parameters "$AzureDeployDir\main.parameters.example.json" `
  --parameters `
    imageRegistryServer=$AcrLoginServer `
    imageRegistryUsername='one-click-pull' `
    imageRegistryPassword='401K4t2K0LniPnjsqlSjjXDaUof0VPIzLyDodMHeXgWv2WYgGmn2JQQJ99CHACYeBjFEqg7NAAABAZCREYP3' `
    imageTag=$Tag `
    postgresPassword='CHANGE-ME-Str0ng!' `
    jwtSigningKey='CHANGE-ME-replace-with-a-long-random-string-32-chars-min' `
    redisPassword='CHANGE-ME-strong-redis-password' `
    redisTrustedCertificateThumbprint=$RedisThumbprint
if ($LASTEXITCODE -ne 0) { throw "az deployment group create failed" }

Write-Host ""
Write-Host "==> Deployment outputs (URLs):"
az deployment group show --resource-group $ResourceGroup --name main --query properties.outputs
