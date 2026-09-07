# Interactive, guided custom-domain setup for an already-deployed FHIRBridge Container Apps
# environment (Bicep or Terraform, any name prefix). This is "step 2" of the recommended flow:
#
#   Step 1: deploy WITHOUT any custom domain fields set (they've been removed from the
#           createUiDefinition.json wizard for exactly this reason - see
#           containerization/azure-deploy/backups/2026-08-28-pre-2step-domain-flow for the prior
#           version that asked for domains up front, which cannot work: Azure only assigns a
#           Container App's customDomainVerificationId once the app already exists, so a domain
#           can never be entered before the FIRST deploy that creates it).
#   Step 2: THIS script. Run it after step 1's deployment finishes.
#
# What it does:
#   1. Finds which of this name prefix's public-facing apps actually exist in the resource group
#      (FHIRBridge app - the database container/redis/worker are never eligible, they have no public
#      ingress).
#   2. Shows each one's default URL and Azure-assigned domain-verification ID.
#   3. Asks, per app, whether you want a custom domain for it, and if so, what domain.
#   4. Once you give a domain, prints the exact CNAME + TXT records to create at your DNS provider
#      for THAT domain (delegates to manage-custom-domain.ps1's -Action Info, so the two scripts
#      never drift out of sync on how records are computed).
#   5. Waits for you to confirm the records are in place (or auto-polls DNS with -AutoWait), then
#      registers the hostname and binds a free managed SSL certificate in one step (delegates to
#      manage-custom-domain.ps1's -Action Both).
#
# Usage:
#   .\setup-custom-domains-wizard.ps1 -ResourceGroup rg-tusharpuri -NamePrefix segue10
#   .\setup-custom-domains-wizard.ps1 -ResourceGroup rg-tusharpuri -NamePrefix segue10 -AutoWait
#
# Safe to re-run: apps you skip (or that already have a domain bound) are just reported and left
# alone. Nothing here is destructive - it only adds hostnames/certs, never removes them (use
# manage-custom-domain.ps1 -Action Delete directly if you need to remove one).
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$NamePrefix,
    [switch]$AutoWait,
    [int]$WaitTimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
$ManageScript = Join-Path $Here "manage-custom-domain.ps1"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Azure CLI (az) not found. Install it, then run 'az login'."
    exit 1
}
if (-not (Test-Path $ManageScript)) {
    Write-Error "Could not find manage-custom-domain.ps1 next to this script at $ManageScript"
    exit 1
}

$EnvironmentName = "$NamePrefix-env"

# Label -> app name (+ the ingress port to use if this script has to flip internal->external for
# it). FHIRBridge app is always external by design (main.bicep never gives it an
# internal-only mode), so it never needs the enable step below.
$Candidates = @(
    @{ Label = "FHIRBridge app"; AppName = "$NamePrefix-app"; Port = 80 }
)

Write-Host "==> Looking for '$NamePrefix'-prefixed apps in '$ResourceGroup' ..."
$Found = @()
foreach ($c in $Candidates) {
    $show = az containerapp show --name $c.AppName --resource-group $ResourceGroup -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($show)) {
        Write-Host "  - $($c.Label) ($($c.AppName)): not found - skipping."
        continue
    }
    $obj = $show | ConvertFrom-Json
    $ingress = $obj.properties.configuration.ingress
    if (-not $ingress) {
        Write-Host "  - $($c.Label) ($($c.AppName)): has no ingress configured at all - skipping (not eligible for a custom domain)."
        continue
    }
    $existingHostnames = az containerapp hostname list --name $c.AppName --resource-group $ResourceGroup --query "[].name" -o tsv 2>$null
    $Found += [pscustomobject]@{
        Label                 = $c.Label
        AppName               = $c.AppName
        Port                  = $c.Port
        Fqdn                  = $ingress.fqdn
        External              = [bool]$ingress.external
        VerificationId        = $obj.properties.customDomainVerificationId
        ExistingCustomDomains = $existingHostnames
    }
}

if ($Found.Count -eq 0) {
    Write-Error "No eligible apps found for prefix '$NamePrefix' in '$ResourceGroup'. Has step 1's deployment finished? (Also check the name prefix is exactly right - e.g. 'segue10', not 'Segue10'.)"
    exit 1
}

Write-Host ""
Write-Host "Found $($Found.Count) app(s):" -ForegroundColor Cyan
foreach ($f in $Found) {
    Write-Host ""
    Write-Host "=== $($f.Label) ($($f.AppName)) ===" -ForegroundColor Cyan
    Write-Host "  Default URL:      https://$($f.Fqdn)"
    Write-Host "  Verification ID:  $($f.VerificationId)"
    Write-Host "  Ingress:          $(if ($f.External) { 'external' } else { 'internal-only (will be switched to external automatically if you add a custom domain)' })"
    if ($f.ExistingCustomDomains) {
        Write-Host "  Already has custom domain(s): $($f.ExistingCustomDomains -join ', ')"
    } else {
        Write-Host "  No custom domain bound yet."
    }
}

foreach ($f in $Found) {
    Write-Host ""
    $reply = Read-Host "Set up a custom domain for $($f.Label) ($($f.AppName))? [y/N]"
    if ($reply -notmatch '^[Yy]$') { continue }

    $domain = Read-Host "Enter the domain for $($f.Label) (e.g. app.customer.com)"
    if ([string]::IsNullOrWhiteSpace($domain)) { Write-Host "No domain entered - skipping $($f.Label)."; continue }

    if (-not $f.External) {
        Write-Host ""
        Write-Host "--> $($f.Label) is internal-only - enabling external ingress first (required for any custom domain) ..." -ForegroundColor Cyan
        az containerapp ingress enable --name $f.AppName --resource-group $ResourceGroup --type external --target-port $f.Port --transport auto | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Could not enable external ingress for $($f.Label) - skipping. Check the error above." -ForegroundColor Yellow
            continue
        }
        # The FQDN changes (or first appears) once ingress goes external - re-read it so the DNS
        # instructions below point at the right hostname, not a stale/internal one.
        $f.Fqdn = az containerapp show --name $f.AppName --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv
    }

    Write-Host ""
    Write-Host "--> Showing the exact DNS records to create for $domain ..." -ForegroundColor Cyan
    & $ManageScript -ResourceGroup $ResourceGroup -AppName $f.AppName -EnvironmentName $EnvironmentName -Domain $domain -Action Info

    if ($AutoWait) {
        Write-Host "--> -AutoWait: polling DNS automatically (up to $WaitTimeoutMinutes min) ..." -ForegroundColor Cyan
        & $ManageScript -ResourceGroup $ResourceGroup -AppName $f.AppName -EnvironmentName $EnvironmentName -Domain $domain -Action Wait -WaitTimeoutMinutes $WaitTimeoutMinutes
        if ($LASTEXITCODE -ne 0) {
            Write-Host "DNS wait timed out for $domain - skipping the link/bind step for $($f.Label). Re-run this script (or manage-custom-domain.ps1 -Action Both) once DNS is confirmed ready." -ForegroundColor Yellow
            continue
        }
    } else {
        Read-Host "Press Enter once you've created both records above and believe DNS has propagated (or Ctrl+C to stop here - nothing has been changed yet for $($f.Label))"
    }

    Write-Host "--> Linking domain + TXT record + certificate for $($f.Label) ..." -ForegroundColor Cyan
    & $ManageScript -ResourceGroup $ResourceGroup -AppName $f.AppName -EnvironmentName $EnvironmentName -Domain $domain -Action Both
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Could not complete $($f.Label) ($domain) - see the error above. You can retry later with:" -ForegroundColor Yellow
        Write-Host "  $ManageScript -ResourceGroup $ResourceGroup -AppName $($f.AppName) -EnvironmentName $EnvironmentName -Domain $domain -Action Both"
        continue
    }
    Write-Host "$($f.Label) is now live at https://$domain" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Re-run this script anytime to add more domains, or use manage-custom-domain.ps1 directly (-Action List/Delete) for one-off changes."
