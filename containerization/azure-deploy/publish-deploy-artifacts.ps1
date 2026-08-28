# Publish compiled Deploy-to-Azure artifacts to the public blob container.
#
# Uploads main.json + createUiDefinition.json (Step 1: full-stack deploy) and custom-domain.json +
# createUiDefinition.custom-domain.json (Step 2: domain/certificate binder - see
# custom-domain.bicep), then prints both Portal deep-links. Step 2 has no certificate checkbox at
# all - run the SAME deploy (same name prefix, same domain(s)) twice: the first run registers the
# hostname only; once its DNS records have propagated, running the exact same deploy again detects
# the domain is already registered and automatically creates + binds the managed certificate
# instead (custom-domain.bicep auto-detects this per app from live state). Each tab in the wizard
# shows each app's name, default URL, and live domain-verification ID (Microsoft.Solutions.
# ArmApiControl), plus which of the two actions this deploy is about to take.
# Requires: az login. Tries --auth-mode login (Storage Blob Data Contributor RBAC role) first,
# then falls back to --auth-mode key (account-key access, via Contributor/Owner's key-list
# permission - a separate, control-plane permission path from the Blob Data RBAC role above, and
# the one that actually worked when this was last run: the account in use has Contributor-level
# access to the storage account but not the Blob Data Contributor data-plane role).
#
# Usage:
#   .\publish-deploy-artifacts.ps1 -StorageAccount fhirbridgedeploy -ResourceGroup rg-tusharpuri
#   .\publish-deploy-artifacts.ps1 -StorageAccount fhirbridgedeploy -ResourceGroup rg-tusharpuri -Container deploy
#
param(
    [Parameter(Mandatory = $true)][string]$StorageAccount,
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [string]$Container = "deploy"
)

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot

if (-not (Test-Path (Join-Path $Here "main.json"))) {
    Write-Host "Building main.json from main.bicep..."
    az bicep build --file (Join-Path $Here "main.bicep") --outfile (Join-Path $Here "main.json")
    if ($LASTEXITCODE -ne 0) { Write-Error "az bicep build failed"; exit $LASTEXITCODE }
}
if (-not (Test-Path (Join-Path $Here "custom-domain.json"))) {
    Write-Host "Building custom-domain.json from custom-domain.bicep..."
    az bicep build --file (Join-Path $Here "custom-domain.bicep") --outfile (Join-Path $Here "custom-domain.json")
    if ($LASTEXITCODE -ne 0) { Write-Error "az bicep build failed"; exit $LASTEXITCODE }
}

function Upload-Blob([string]$Name, [string]$FilePath) {
    # $ErrorActionPreference = "Stop" (script-wide) turns az CLI's stderr output into a terminating
    # PowerShell error even with 2>$null redirection - try/catch is required to actually attempt
    # the first auth mode and fall back on failure instead of the whole script aborting here.
    try {
        az storage blob upload --account-name $StorageAccount --container-name $Container `
            --name $Name --file $FilePath --overwrite --auth-mode login --only-show-errors 2>$null
        if ($LASTEXITCODE -eq 0) { return }
    } catch {}

    Write-Host "  --auth-mode login failed for $Name (likely missing the Storage Blob Data Contributor RBAC role) - retrying with --auth-mode key ..."
    az storage blob upload --account-name $StorageAccount --container-name $Container `
        --name $Name --file $FilePath --overwrite --auth-mode key
    if ($LASTEXITCODE -ne 0) { Write-Error "$Name upload failed with both auth modes"; exit $LASTEXITCODE }
}

Write-Host "Uploading main.json, createUiDefinition.json, custom-domain.json, and createUiDefinition.custom-domain.json to $StorageAccount/$Container ..."
Upload-Blob -Name "main.json" -FilePath (Join-Path $Here "main.json")
Upload-Blob -Name "createUiDefinition.json" -FilePath (Join-Path $Here "createUiDefinition.json")
Upload-Blob -Name "custom-domain.json" -FilePath (Join-Path $Here "custom-domain.json")
Upload-Blob -Name "createUiDefinition.custom-domain.json" -FilePath (Join-Path $Here "createUiDefinition.custom-domain.json")

# Cache-busting query param - the Azure portal / browser has been observed to serve a
# previously-fetched copy of these blobs even after -overwrite replaces their content, since
# nothing here sets a no-cache Cache-Control header. Appending a changing ?v= forces a fresh fetch.
$CacheBust = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$MainJsonUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name main.json -o tsv) + "?v=$CacheBust"
$UiDefUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.json -o tsv) + "?v=$CacheBust"
$CustomDomainJsonUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name custom-domain.json -o tsv) + "?v=$CacheBust"
$CustomDomainUiDefUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.custom-domain.json -o tsv) + "?v=$CacheBust"
$DeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($MainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($UiDefUrl))"
$CustomDomainDeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($CustomDomainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($CustomDomainUiDefUrl))"

Write-Host ""
Write-Host "Step 1 - Deploy-to-Azure URL (full stack):" -ForegroundColor Cyan
Write-Host $DeployUrl
Write-Host ""
Write-Host "Step 2 - Custom domain / certificate binding URL (run after step 1; run it again with the same domain(s) once DNS has propagated to bind the certificate):" -ForegroundColor Cyan
Write-Host $CustomDomainDeployUrl
Write-Host ""
Write-Host "Opened step 1 in browser (if supported)."
try { Start-Process $DeployUrl } catch { }
