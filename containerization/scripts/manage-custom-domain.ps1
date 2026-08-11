# Manage custom domain + Azure-managed SSL for a Segue / FHIRBridge Container App.
#
# Supports Mitul's operator path (Info → DNS → Add → Bind) against any already-deployed
# Container App (Bicep or Terraform). Prefer the Bicep two-phase wizard for clients:
#   Phase 1: domain + bindCustomDomainCertificates=false (hostname only)
#   Phase 2: same domain + bindCustomDomainCertificates=true (managed cert + SNI)
#
# IaC drift: if you bind with this script, keep the matching domain (+ bind flag) in
# Bicep/TF params on later applies or the hostname may be removed.
#
# Usage:
#   .\manage-custom-domain.ps1 -ResourceGroup rg-xxx -AppName segue5-app `
#     -EnvironmentName segue5-env -Domain app.contoso.com -Action Info
#   .\manage-custom-domain.ps1 ... -Action Both -ValidationMethod CNAME
#
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$AppName,
    [Parameter(Mandatory = $true)][string]$EnvironmentName,
    [Parameter(Mandatory = $true)][string]$Domain,
    [Parameter(Mandatory = $true)]
    [ValidateSet("Info", "Wait", "Add", "Bind", "Both", "List", "Delete")]
    [string]$Action,
    [ValidateSet("CNAME", "HTTP", "TXT")][string]$ValidationMethod = "CNAME",
    [int]$WaitTimeoutMinutes = 30,
    [int]$WaitPollSeconds = 30
)

$ErrorActionPreference = "Stop"

function Assert-AzCli {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Write-Error "Azure CLI (az) not found. Install it, then run 'az login'."
        exit 1
    }
    $acct = az account show 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($acct)) {
        Write-Error "Not logged in to Azure CLI. Run: az login"
        exit 1
    }
}

function Get-AppIngressFqdn {
    $fqdn = az containerapp show --name $AppName --resource-group $ResourceGroup `
        --query "properties.configuration.ingress.fqdn" -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($fqdn)) {
        Write-Error "Could not read ingress FQDN for '$AppName'."
        exit 1
    }
    return $fqdn.Trim()
}

function Get-DomainVerificationId {
    $id = az containerapp show --name $AppName --resource-group $ResourceGroup `
        --query "properties.customDomainVerificationId" -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($id)) {
        Write-Error "Could not read customDomainVerificationId for '$AppName'."
        exit 1
    }
    return $id.Trim()
}

function Show-Info {
    $fqdn = Get-AppIngressFqdn
    $verificationId = Get-DomainVerificationId
    Write-Host ""
    Write-Host "=== Custom domain DNS instructions ===" -ForegroundColor Cyan
    Write-Host "App:            $AppName"
    Write-Host "Resource group: $ResourceGroup"
    Write-Host "Environment:    $EnvironmentName"
    Write-Host "Custom domain:  $Domain"
    Write-Host "Default FQDN:   $fqdn"
    Write-Host ""
    Write-Host "Create at your DNS provider:" -ForegroundColor Yellow
    Write-Host "  CNAME  $Domain           -> $fqdn"
    Write-Host "  TXT    asuid.$Domain     -> $verificationId"
    Write-Host ""
    Write-Host "Then: -Action Wait  (optional)  then  -Action Both"
    Write-Host "Or Bicep Phase 2: redeploy with bindCustomDomainCertificates=true"
    Write-Host ""
}

function Test-DnsReady {
    $fqdn = Get-AppIngressFqdn
    $verificationId = Get-DomainVerificationId
    $cnameOk = $false
    $txtOk = $false
    try {
        $cname = Resolve-DnsName -Name $Domain -Type CNAME -ErrorAction SilentlyContinue
        if ($cname) {
            $targets = @($cname | ForEach-Object { $_.NameHost }) -join " "
            if ($targets -match [regex]::Escape($fqdn.TrimEnd('.'))) { $cnameOk = $true }
            Write-Host "CNAME $Domain -> $targets $(if ($cnameOk) { '[OK]' } else { '[MISMATCH]' })"
        } else { Write-Host "CNAME $Domain -> (not found)" }
    } catch { Write-Host "CNAME lookup failed: $($_.Exception.Message)" }
    try {
        $txt = Resolve-DnsName -Name "asuid.$Domain" -Type TXT -ErrorAction SilentlyContinue
        if ($txt) {
            $values = @($txt | ForEach-Object { $_.Strings }) -join " "
            if ($values -match [regex]::Escape($verificationId)) { $txtOk = $true }
            Write-Host "TXT asuid.$Domain -> $values $(if ($txtOk) { '[OK]' } else { '[MISMATCH]' })"
        } else { Write-Host "TXT asuid.$Domain -> (not found)" }
    } catch { Write-Host "TXT lookup failed: $($_.Exception.Message)" }
    return ($cnameOk -and $txtOk)
}

function Invoke-Wait {
    $deadline = (Get-Date).AddMinutes($WaitTimeoutMinutes)
    Write-Host "Waiting up to $WaitTimeoutMinutes min for DNS..." -ForegroundColor Cyan
    while ((Get-Date) -lt $deadline) {
        if (Test-DnsReady) { Write-Host "DNS looks ready." -ForegroundColor Green; return }
        Start-Sleep -Seconds $WaitPollSeconds
    }
    Write-Error "Timed out waiting for DNS (also check CAA records)."
    exit 1
}

function Invoke-Add {
    Write-Host "Adding hostname '$Domain' (no certificate yet)..." -ForegroundColor Cyan
    az containerapp hostname add --hostname $Domain --name $AppName --resource-group $ResourceGroup
    if ($LASTEXITCODE -ne 0) { Write-Error "hostname add failed"; exit $LASTEXITCODE }
    Write-Host "Hostname added." -ForegroundColor Green
}

function Invoke-Bind {
    Write-Host "Binding managed certificate for '$Domain' ($ValidationMethod)..." -ForegroundColor Cyan
    az containerapp hostname bind --hostname $Domain --name $AppName --resource-group $ResourceGroup `
        --environment $EnvironmentName --validation-method $ValidationMethod
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Bind failed. Check DNS/CAA or stuck certs (az containerapp env certificate list)." -ForegroundColor Yellow
        Write-Error "hostname bind failed"
        exit $LASTEXITCODE
    }
    Write-Host "Bind requested. Verify https://$Domain" -ForegroundColor Green
    az containerapp env certificate list --name $EnvironmentName --resource-group $ResourceGroup `
        --query "[].{Name:name, State:properties.provisioningState, Subject:properties.subjectName}" -o table
}

function Show-List {
    az containerapp hostname list --name $AppName --resource-group $ResourceGroup -o table
    az containerapp env certificate list --name $EnvironmentName --resource-group $ResourceGroup `
        --query "[].{Name:name, State:properties.provisioningState, Subject:properties.subjectName}" -o table
}

function Invoke-Delete {
    az containerapp hostname delete --hostname $Domain --name $AppName --resource-group $ResourceGroup --yes
    if ($LASTEXITCODE -ne 0) { Write-Error "hostname delete failed"; exit $LASTEXITCODE }
    Write-Host "Hostname removed."
}

Assert-AzCli
switch ($Action) {
    "Info" { Show-Info }
    "Wait" { Invoke-Wait }
    "Add" { Invoke-Add }
    "Bind" { Invoke-Bind }
    "Both" { Invoke-Add; Invoke-Bind }
    "List" { Show-List }
    "Delete" { Invoke-Delete }
}
