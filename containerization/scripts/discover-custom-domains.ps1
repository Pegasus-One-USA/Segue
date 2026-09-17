# Read-only discovery helper for the Path A custom-domain flow (custom-domain.bicep - see
# containerization/azure-deploy/CUSTOM_DOMAIN_SELF_SERVICE.md). Finds the public-facing app for
# a given name prefix (Segue app), shows its default URL and
# Azure-assigned domain-verification ID, and - once you type in a domain for an app - prints
# the exact CNAME + TXT records to create at your DNS provider, plus ready-to-run
# custom-domain.bicep deploy commands for both phases (hostname-only, then certificate-bind).
#
# This makes NO changes itself - every call here is a read-only `az containerapp show`. Actual
# binding happens when you run the printed custom-domain.bicep commands (or the equivalent
# Deploy-to-Azure link) yourself.
#
# Usage:
#   .\discover-custom-domains.ps1                                   # prompts for both values
#   .\discover-custom-domains.ps1 -ResourceGroup rg-tusharpuri -NamePrefix segue12
param(
    [string]$ResourceGroup,
    [string]$NamePrefix
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = Read-Host "Resource group"
}
if ([string]::IsNullOrWhiteSpace($NamePrefix)) {
    $NamePrefix = Read-Host "Name prefix (e.g. segue12)"
}

# the database container/redis/worker are never eligible - they have no public ingress, so no
# custom domain is possible for them.
$Candidates = @(
    @{ Label = "Segue app"; AppName = "$NamePrefix-app" }
)

Write-Host ""
Write-Host "==> Looking for '$NamePrefix'-prefixed apps in '$ResourceGroup' ..." -ForegroundColor Cyan
$Found = @()
foreach ($c in $Candidates) {
    $show = az containerapp show --name $c.AppName --resource-group $ResourceGroup -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($show)) {
        Write-Host "  - $($c.Label) ($($c.AppName)): not found - skipping."
        continue
    }
    $obj = $show | ConvertFrom-Json
    $Found += [pscustomobject]@{
        Label          = $c.Label
        AppName        = $c.AppName
        Fqdn           = $obj.properties.configuration.ingress.fqdn
        VerificationId = $obj.properties.customDomainVerificationId
    }
}

if ($Found.Count -eq 0) {
    Write-Error "No apps found for prefix '$NamePrefix' in '$ResourceGroup'. Check the resource group and prefix are exactly right (e.g. 'segue12', not 'Segue12')."
    exit 1
}

Write-Host ""
Write-Host "Found $($Found.Count) app(s):" -ForegroundColor Cyan
foreach ($f in $Found) {
    Write-Host ""
    Write-Host "=== $($f.Label) ($($f.AppName)) ===" -ForegroundColor Cyan
    Write-Host "  Default URL:             https://$($f.Fqdn)"
    Write-Host "  Domain verification ID: $($f.VerificationId)"
}

$EnvironmentName = "$NamePrefix-env"

Write-Host ""
Write-Host "Enter a domain per app to see its DNS records and deploy commands (leave blank to skip that app)."
foreach ($f in $Found) {
    $domain = Read-Host "Domain for $($f.Label) ($($f.AppName))"
    if ([string]::IsNullOrWhiteSpace($domain)) { continue }

    Write-Host ""
    Write-Host "--- DNS records to create for $domain ($($f.Label)) ---" -ForegroundColor Green
    Write-Host "  CNAME  $domain          -> $($f.Fqdn)"
    Write-Host "  TXT    asuid.$domain    -> $($f.VerificationId)"
    Write-Host ""
    Write-Host "Once those records are live and propagated, run (Phase 1 - hostname only, no cert yet):"
    Write-Host "  az deployment group create -g $ResourceGroup -f containerization/azure-deploy/custom-domain.bicep -p environmentName=$EnvironmentName appName=$($f.AppName) domain=$domain bindCertificate=false"
    Write-Host ""
    Write-Host "Then (Phase 2 - bind the managed certificate, only after DNS is confirmed propagated):"
    Write-Host "  az deployment group create -g $ResourceGroup -f containerization/azure-deploy/custom-domain.bicep -p environmentName=$EnvironmentName appName=$($f.AppName) domain=$domain bindCertificate=true"
    Write-Host ""
}

Write-Host "Done - this script made no changes. Re-run anytime; nothing here is destructive."
