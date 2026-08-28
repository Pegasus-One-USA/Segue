# Publish compiled Deploy-to-Azure artifacts to the public blob container.
#
# Uploads main.json + createUiDefinition.json, then prints the Portal deep-link.
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

Write-Host "Uploading main.json and createUiDefinition.json to $StorageAccount/$Container ..."
Upload-Blob -Name "main.json" -FilePath (Join-Path $Here "main.json")
Upload-Blob -Name "createUiDefinition.json" -FilePath (Join-Path $Here "createUiDefinition.json")

$MainJsonUrl = az storage blob url --account-name $StorageAccount --container-name $Container --name main.json -o tsv
$UiDefUrl = az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.json -o tsv
$DeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($MainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($UiDefUrl))"

Write-Host ""
Write-Host "Deploy-to-Azure URL:" -ForegroundColor Cyan
Write-Host $DeployUrl
Write-Host ""
Write-Host "Opened in browser (if supported)."
try { Start-Process $DeployUrl } catch { }
