# Publish compiled Deploy-to-Azure artifacts to the public blob container.
#
# Uploads main.json + createUiDefinition.json, then prints the Portal deep-link.
# Requires: az login, and Storage Blob Data Contributor (or account key access) on the storage account.
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

Write-Host "Uploading main.json and createUiDefinition.json to $StorageAccount/$Container ..."
az storage blob upload --account-name $StorageAccount --container-name $Container `
    --name main.json --file (Join-Path $Here "main.json") --overwrite --auth-mode login
if ($LASTEXITCODE -ne 0) { Write-Error "main.json upload failed (need Blob Data role?)"; exit $LASTEXITCODE }

az storage blob upload --account-name $StorageAccount --container-name $Container `
    --name createUiDefinition.json --file (Join-Path $Here "createUiDefinition.json") --overwrite --auth-mode login
if ($LASTEXITCODE -ne 0) { Write-Error "createUiDefinition.json upload failed"; exit $LASTEXITCODE }

$MainJsonUrl = az storage blob url --account-name $StorageAccount --container-name $Container --name main.json -o tsv
$UiDefUrl = az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.json -o tsv
$DeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($MainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($UiDefUrl))"

Write-Host ""
Write-Host "Deploy-to-Azure URL:" -ForegroundColor Cyan
Write-Host $DeployUrl
Write-Host ""
Write-Host "Opened in browser (if supported)."
try { Start-Process $DeployUrl } catch { }
