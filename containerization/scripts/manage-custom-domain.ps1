# Adds (and/or binds a free managed certificate for) a custom domain on an already-deployed
# Container App, directly via the Container Apps CLI -- no Terraform apply / Bicep-ARM redeploy
# needed for either step. This is the genuine 2-step split Azure itself supports
# (`az containerapp hostname add` then `az containerapp hostname bind`), used here instead of going
# through the Terraform environment's *_custom_domain/*_ssl_enabled variables or main.bicep's
# fhirbridgeAppCustomDomain/SslEnabled parameters + a full apply/redeploy each time. Works
# identically against a Container App regardless of which IaC tool created it -- these are plain
# `az containerapp` calls against the resource itself, nothing Terraform- or Bicep-specific.
#
# CAVEAT -- both the Terraform environment and main.bicep also manage each app's customDomains
# declaratively. If you use this script to add/bind a domain and then LATER `terraform apply` /
# redeploy the wizard with that app's custom-domain variable/parameter left blank, that apply will
# remove what this script added (both tools reconcile the full declared state on every run). Once
# you start managing a domain with this script, either keep the matching variable/parameter filled
# in on every future apply/redeploy too, or manage that domain exclusively through this script from
# then on -- don't mix the two for the same app.
#
# Usage:
#   # Step 1: just print the CNAME target + TXT verification value you need to add at your DNS
#   # provider -- makes no changes.
#   ./manage-custom-domain.ps1 -ResourceGroup rg-tusharpuri -AppName segue5-app -EnvironmentName segue5-env -Domain app.example.com -Action Info
#
#   # Step 2: once those DNS records resolve, register the domain (no certificate yet).
#   ./manage-custom-domain.ps1 -ResourceGroup rg-tusharpuri -AppName segue5-app -EnvironmentName segue5-env -Domain app.example.com -Action Add
#
#   # Step 3: once the TXT record is confirmed, issue + bind the free managed certificate.
#   ./manage-custom-domain.ps1 -ResourceGroup rg-tusharpuri -AppName segue5-app -EnvironmentName segue5-env -Domain app.example.com -Action Bind
#
#   # Or do steps 2+3 in one call, if DNS is already fully in place:
#   ./manage-custom-domain.ps1 -ResourceGroup rg-tusharpuri -AppName segue5-app -EnvironmentName segue5-env -Domain app.example.com -Action Both
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$AppName,

    [Parameter(Mandatory = $true)]
    [string]$EnvironmentName,

    [Parameter(Mandatory = $true)]
    [string]$Domain,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Info", "Add", "Bind", "Both")]
    [string]$Action,

    # TXT worked for freeddns.org-style shared dynamic-DNS domains when CNAME didn't (Azure's
    # InvalidValidationMethod error names the actual supported methods per-domain) -- CNAME is the
    # normal choice for a subdomain you fully control; switch to HTTP only for a true apex domain.
    [ValidateSet("CNAME", "HTTP", "TXT")]
    [string]$ValidationMethod = "TXT"
)

$ErrorActionPreference = "Stop"

Write-Host "== $AppName's current default hostname + domain verification code =="
$fqdn = az containerapp show --name $AppName --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv
$verificationId = az containerapp show --name $AppName --resource-group $ResourceGroup --query "properties.customDomainVerificationId" -o tsv
Write-Host "  CNAME: $Domain -> $fqdn"
Write-Host "  TXT:   asuid.$Domain -> $verificationId"
Write-Host ""

if ($Action -eq "Info") {
    Write-Host "Add both records above at your DNS provider, confirm they resolve, then re-run with -Action Add."
    exit 0
}

Write-Host "Make sure both DNS records above are already in place and resolving before continuing (nslookup -type=CNAME / -type=TXT) -- this will fail with a clear error if they aren't."
Write-Host ""

if ($Action -eq "Add" -or $Action -eq "Both") {
    Write-Host "== Registering $Domain on $AppName (no certificate yet) =="
    az containerapp hostname add --hostname $Domain --resource-group $ResourceGroup --name $AppName
}

if ($Action -eq "Bind" -or $Action -eq "Both") {
    Write-Host "== Issuing + binding a free managed certificate for $Domain (validation: $ValidationMethod) =="
    az containerapp hostname bind --hostname $Domain --resource-group $ResourceGroup --name $AppName --environment $EnvironmentName --validation-method $ValidationMethod
}

Write-Host ""
Write-Host "== Current custom domains on $AppName =="
az containerapp hostname list --resource-group $ResourceGroup --name $AppName -o table
