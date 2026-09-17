# Automates the custom-domain.bicep flow end to end: reads each app's default FQDN + domain
# verification ID directly from the live container app (no deploy needed for this - main.bicep
# already assigns it the moment the app exists), prints the CNAME + asuid TXT records to create,
# polls DNS until they resolve, then deploys custom-domain.bicep ONCE - it registers the
# hostname, creates the certificate, and binds it, all in that one deploy.
#
# Usage:
#   .\auto-bind-custom-domain.ps1 -ResourceGroup rg-tusharpuri -NamePrefix segue13 -SegueAppDomain segueapp.pegasusone.com
#
# Safe to re-run: custom-domain.bicep is idempotent either way (a domain that's already bound is
# just re-affirmed, not disturbed in any lasting way).
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$NamePrefix,
    [string]$SegueAppDomain = "",
    [int]$WaitTimeoutMinutes = 30,
    [int]$WaitPollSeconds = 30
)

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
$BicepFile = Join-Path $Here "..\azure-deploy\custom-domain.bicep"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Azure CLI (az) not found. Install it, then run 'az login'."
    exit 1
}
if (-not (Test-Path $BicepFile)) {
    Write-Error "Could not find custom-domain.bicep at $BicepFile"
    exit 1
}

$Requested = @(
    @{ Label = "segueApp"; AppName = "$NamePrefix-app"; Domain = $SegueAppDomain }
) | Where-Object { $_.Domain }

if ($Requested.Count -eq 0) {
    Write-Error "Set -SegueAppDomain."
    exit 1
}

function Test-DnsReady([string]$Domain, [string]$Fqdn, [string]$VerificationId) {
    $cnameOk = $false
    $txtOk = $false
    try {
        $cname = Resolve-DnsName -Name $Domain -Type CNAME -ErrorAction SilentlyContinue
        if ($cname) {
            $targets = @($cname | ForEach-Object { $_.NameHost }) -join " "
            if ($targets -match [regex]::Escape($Fqdn.TrimEnd('.'))) { $cnameOk = $true }
        }
    } catch {}
    try {
        $txt = Resolve-DnsName -Name "asuid.$Domain" -Type TXT -ErrorAction SilentlyContinue
        if ($txt) {
            $values = @($txt | ForEach-Object { $_.Strings }) -join " "
            if ($values -match [regex]::Escape($VerificationId)) { $txtOk = $true }
        }
    } catch {}
    return ($cnameOk -and $txtOk)
}

# --- Read each requested app's current FQDN + verification ID directly - no deploy needed, these
# already exist the moment main.bicep created the app. ---
Write-Host "==> Reading app details ..." -ForegroundColor Cyan
$Pending = @()
foreach ($r in $Requested) {
    $show = az containerapp show --name $r.AppName --resource-group $ResourceGroup -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($show)) {
        Write-Error "Could not find app '$($r.AppName)' in '$ResourceGroup' - has Step 1 finished?"
        exit 1
    }
    $obj = $show | ConvertFrom-Json
    $fqdn = $obj.properties.configuration.ingress.fqdn
    $verificationId = $obj.properties.customDomainVerificationId
    $alreadyBound = $obj.properties.configuration.ingress.customDomains |
        Where-Object { $_.name -eq $r.Domain -and $_.bindingType -eq 'SniEnabled' }

    Write-Host ""
    Write-Host "=== $($r.Label) ($($r.AppName)) - $($r.Domain) ===" -ForegroundColor Cyan
    if ($alreadyBound) {
        Write-Host "  Already certificate-bound. Live at https://$($r.Domain)" -ForegroundColor Green
        continue
    }
    Write-Host "  CNAME  $($r.Domain)          -> $fqdn"
    Write-Host "  TXT    asuid.$($r.Domain)    -> $verificationId"
    $Pending += [pscustomobject]@{ Label = $r.Label; AppName = $r.AppName; Domain = $r.Domain; Fqdn = $fqdn; VerificationId = $verificationId }
}

if ($Pending.Count -eq 0) {
    Write-Host ""
    Write-Host "All requested domain(s) already have a bound certificate. Nothing further to do." -ForegroundColor Green
    exit 0
}

# --- Create the DNS records, then poll until every pending domain resolves correctly ---
Write-Host ""
Write-Host "==> Create the record(s) above at your DNS provider now." -ForegroundColor Yellow
Write-Host "==> Polling DNS for $($Pending.Count) domain(s) (up to $WaitTimeoutMinutes min) ..." -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes($WaitTimeoutMinutes)
while ($true) {
    $stillPending = $Pending | Where-Object { -not (Test-DnsReady -Domain $_.Domain -Fqdn $_.Fqdn -VerificationId $_.VerificationId) }
    if ($stillPending.Count -eq 0) {
        Write-Host "DNS looks ready for all pending domain(s)." -ForegroundColor Green
        break
    }
    if ((Get-Date) -ge $deadline) {
        Write-Error "Timed out after $WaitTimeoutMinutes min waiting for DNS on: $(($stillPending | ForEach-Object { $_.Domain }) -join ', '). Re-run this script once DNS is confirmed propagated (also check CAA records)."
        exit 1
    }
    Write-Host "  Still waiting on: $(($stillPending | ForEach-Object { $_.Domain }) -join ', ') ..."
    Start-Sleep -Seconds $WaitPollSeconds
}

# --- One deploy: custom-domain.bicep registers the hostname, creates the certificate, and binds
# it, all in this same deploy, now that DNS is ready. ---
Write-Host ""
Write-Host "==> Deploying custom-domain.bicep (namePrefix=$NamePrefix) ..." -ForegroundColor Cyan
$paramArgs = @("namePrefix=$NamePrefix")
if ($SegueAppDomain) { $paramArgs += "segueAppDomain=$SegueAppDomain" }

$outputJson = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file $BicepFile `
    --parameters $paramArgs `
    --query "properties.outputs.results.value" -o json
if ($LASTEXITCODE -ne 0) {
    Write-Error "Deployment failed - see the error above."
    exit $LASTEXITCODE
}

Write-Host ""
$results = $outputJson | ConvertFrom-Json
foreach ($r in $results) {
    Write-Host "$($r.app) is now live at $($r.customDomainUrl)" -ForegroundColor Green
}
