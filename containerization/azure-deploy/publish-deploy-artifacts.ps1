# Publish compiled Deploy-to-Azure artifacts to the public blob container.
#
# Uploads main.json + createUiDefinition.json (Step 1: full-stack deploy), custom-domain.json +
# createUiDefinition.custom-domain.json (Step 2: domain/certificate binder - see
# custom-domain.bicep), and 2 limited wizards that reuse main.json against an EXISTING install:
# createUiDefinition.update-application.json (Step 3: version-only update) and
# createUiDefinition.upgrade-resources.json (Step 4: size/SKU-only resize) - see those files' own
# "config.basics.description" for exactly which fields each one exposes vs. leaves at main.bicep's
# defaults. Prints all 4 Portal deep-links. Step 2 is ONE deploy per domain: it registers the
# hostname, creates the managed certificate, and binds it, all in that single deployment (internally
# sequenced via two module calls so Azure only ever sees "certificate created" after "hostname
# added" has actually completed). The one thing you still do yourself first: create the CNAME +
# asuid TXT records at your DNS provider and wait for them to propagate - Azure validates DNS as
# part of this same deploy and rejects it if they are not ready yet
# (InvalidCustomHostNameValidation); just wait and redeploy once they resolve. Each tab in the
# wizard shows each app's name, default URL, and live domain-verification ID (Microsoft.Solutions.
# ArmApiControl) so you never need a throwaway prior deploy just to learn those values.
# containerization/scripts/auto-bind-custom-domain.ps1|sh automates the DNS-wait + deploy for you.
# Requires: az login. Tries --auth-mode login (Storage Blob Data Contributor RBAC role) first,
# then falls back to --auth-mode key (account-key access, via Contributor/Owner's key-list
# permission - a separate, control-plane permission path from the Blob Data RBAC role above, and
# the one that actually worked when this was last run: the account in use has Contributor-level
# access to the storage account but not the Blob Data Contributor data-plane role).
#
# Usage:
#   .\publish-deploy-artifacts.ps1 -StorageAccount seguedeploy -ResourceGroup rg-tusharpuri
#   .\publish-deploy-artifacts.ps1 -StorageAccount seguedeploy -ResourceGroup rg-tusharpuri -Container deploy
#
# -ImageRegistryName / -ImageRepository control where Step 3/4's "Version" dropdown reads its tag
# list from (az acr repository show-tags, newest first, each labeled with its push date) - see
# createUiDefinition.update-application.json / createUiDefinition.upgrade-resources.json's own
# imageTag element comment. Requires the identity running this script to have at least AcrPull /
# Reader on that registry; if the listing fails (or the registry is unreachable), the dropdown
# falls back to a single "latest" entry instead of failing the whole publish.
param(
    [Parameter(Mandatory = $true)][string]$StorageAccount,
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [string]$Container = "deploy",
    [string]$ImageRegistryName = "seguebuilds",
    [string]$ImageRepository = "segue-app"
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

function Get-ImageTagAllowedValuesJson([string]$RegistryName, [string]$Repository) {
    $tagsJson = az acr repository show-tags --name $RegistryName --repository $Repository --orderby time_desc --detail -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagsJson)) {
        Write-Host "  Could not list tags for '$Repository' in registry '$RegistryName' (need AcrPull/Reader on the registry) - Version dropdown will only offer 'latest'." -ForegroundColor Yellow
        return @{ AllowedValuesJson = '[{"label":"latest (tag listing unavailable)","value":"latest"}]'; Default = "latest" }
    }
    $tags = $tagsJson | ConvertFrom-Json
    if (-not $tags -or $tags.Count -eq 0) {
        Write-Host "  Registry '$RegistryName' has no tags yet for '$Repository' - Version dropdown will only offer 'latest'." -ForegroundColor Yellow
        return @{ AllowedValuesJson = '[{"label":"latest (no tags published yet)","value":"latest"}]'; Default = "latest" }
    }
    $options = $tags | ForEach-Object {
        $created = ([DateTimeOffset]::Parse($_.createdTime)).ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " UTC"
        [ordered]@{ label = "$($_.name) - created $created"; value = $_.name }
    }
    return @{ AllowedValuesJson = ($options | ConvertTo-Json -AsArray -Depth 5 -Compress); Default = $options[0].value }
}

# Substitutes the __IMAGE_TAG_ALLOWED_VALUES__/__IMAGE_TAG_DEFAULT__ placeholder tokens in a
# createUiDefinition source file with a live tag list, writing the result to a temp file - the
# checked-in source file itself is never modified, so git stays clean between publishes.
function New-ImageTagDropdownFile([string]$SourcePath, [hashtable]$TagOptions) {
    $content = Get-Content -Path $SourcePath -Raw
    $content = $content.Replace('"__IMAGE_TAG_ALLOWED_VALUES__"', $TagOptions.AllowedValuesJson)
    $content = $content.Replace('__IMAGE_TAG_DEFAULT__', $TagOptions.Default)
    $tempPath = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tempPath -Value $content -Encoding utf8 -NoNewline
    return $tempPath
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

Write-Host "Uploading main.json, createUiDefinition.json, custom-domain.json, createUiDefinition.custom-domain.json, createUiDefinition.update-application.json, and createUiDefinition.upgrade-resources.json to $StorageAccount/$Container ..."
Upload-Blob -Name "main.json" -FilePath (Join-Path $Here "main.json")
Upload-Blob -Name "createUiDefinition.json" -FilePath (Join-Path $Here "createUiDefinition.json")
Upload-Blob -Name "custom-domain.json" -FilePath (Join-Path $Here "custom-domain.json")
Upload-Blob -Name "createUiDefinition.custom-domain.json" -FilePath (Join-Path $Here "createUiDefinition.custom-domain.json")
Write-Host "Fetching '$ImageRepository' tags from registry '$ImageRegistryName' for the Version dropdown..."
$TagOptions = Get-ImageTagAllowedValuesJson -RegistryName $ImageRegistryName -Repository $ImageRepository
$UpdateApplicationTempFile = New-ImageTagDropdownFile -SourcePath (Join-Path $Here "createUiDefinition.update-application.json") -TagOptions $TagOptions
$UpgradeResourcesTempFile = New-ImageTagDropdownFile -SourcePath (Join-Path $Here "createUiDefinition.upgrade-resources.json") -TagOptions $TagOptions
try {
    Upload-Blob -Name "createUiDefinition.update-application.json" -FilePath $UpdateApplicationTempFile
    Upload-Blob -Name "createUiDefinition.upgrade-resources.json" -FilePath $UpgradeResourcesTempFile
} finally {
    Remove-Item -Path $UpdateApplicationTempFile, $UpgradeResourcesTempFile -ErrorAction SilentlyContinue
}

# Cache-busting query param - the Azure portal / browser has been observed to serve a
# previously-fetched copy of these blobs even after -overwrite replaces their content, since
# nothing here sets a no-cache Cache-Control header. Appending a changing ?v= forces a fresh fetch.
$CacheBust = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$MainJsonUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name main.json -o tsv) + "?v=$CacheBust"
$UiDefUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.json -o tsv) + "?v=$CacheBust"
$CustomDomainJsonUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name custom-domain.json -o tsv) + "?v=$CacheBust"
$CustomDomainUiDefUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.custom-domain.json -o tsv) + "?v=$CacheBust"
$UpdateApplicationUiDefUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.update-application.json -o tsv) + "?v=$CacheBust"
$UpgradeResourcesUiDefUrl = (az storage blob url --account-name $StorageAccount --container-name $Container --name createUiDefinition.upgrade-resources.json -o tsv) + "?v=$CacheBust"
$DeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($MainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($UiDefUrl))"
$CustomDomainDeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($CustomDomainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($CustomDomainUiDefUrl))"
$UpdateApplicationDeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($MainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($UpdateApplicationUiDefUrl))"
$UpgradeResourcesDeployUrl = "https://portal.azure.com/#create/Microsoft.Template/uri/$([System.Uri]::EscapeDataString($MainJsonUrl))/createUIDefinitionUri/$([System.Uri]::EscapeDataString($UpgradeResourcesUiDefUrl))"

Write-Host ""
Write-Host "Step 1 - Deploy-to-Azure URL (full stack):" -ForegroundColor Cyan
Write-Host $DeployUrl
Write-Host ""
Write-Host "Step 2 - Custom domain / certificate binding URL (run once per domain, after step 1 and after DNS has propagated):" -ForegroundColor Cyan
Write-Host $CustomDomainDeployUrl
Write-Host ""
Write-Host "Step 3 - Update application (version only) URL - targets an EXISTING install, same namePrefix:" -ForegroundColor Cyan
Write-Host $UpdateApplicationDeployUrl
Write-Host ""
Write-Host "Step 4 - Upgrade resources (sizes/SKUs only) URL - targets an EXISTING install, same namePrefix:" -ForegroundColor Cyan
Write-Host $UpgradeResourcesDeployUrl
Write-Host ""
Write-Host "Opened step 1 in browser (if supported)."
try { Start-Process $DeployUrl } catch { }
