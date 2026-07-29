# Tears down everything the Azure Terraform environment created inside its target resource group -
# ACR, Log Analytics workspace, Container Apps Environment, Key Vault, storage account/shares, and
# all 5 Container Apps. The resource group itself is a pre-existing one this config only deploys
# INTO (see the data "azurerm_resource_group" "main" block in main.tf) - `terraform destroy` removes
# everything Terraform created but leaves the resource group itself intact, since it wasn't created
# by this config and may be shared with other things.
#
# Usage:
#   ./cleanup-azure.ps1          # prompts for confirmation, then terraform destroy
#   ./cleanup-azure.ps1 -Yes     # skip the confirmation prompt (-auto-approve)
param(
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$EnvDir = Join-Path $RepoRoot "containerization/terraform/environments/azure"

Push-Location $EnvDir
try {
    if (-not (Test-Path "terraform.tfstate") -and -not (Test-Path ".terraform")) {
        Write-Error "No local Terraform state found in $EnvDir - nothing to destroy from here. (If state is stored remotely, run 'terraform init' first.)"
        exit 1
    }

    # Read the target resource group name from tfvars (falls back to the variable default) -
    # purely for the tag-filtered preview below, not used by `terraform destroy` itself (that's
    # state-scoped and never touches anything outside what Terraform created, tagged or not).
    $RgName = "rg-tusharpuri"
    if (Test-Path "terraform.tfvars") {
        $match = Select-String -Path "terraform.tfvars" -Pattern '^\s*resource_group_name\s*=\s*"([^"]*)"' | Select-Object -First 1
        if ($match) { $RgName = $match.Matches[0].Groups[1].Value }
    }

    # az CLI rejects combining --tag with --resource-group on `az resource list` ("you cannot use
    # '--tag' with '--resource-group'") - so filter by tag across the subscription instead and
    # narrow to this resource group client-side via --query.
    $azAvailable = [bool](Get-Command az -ErrorAction SilentlyContinue)
    if ($azAvailable) {
        Write-Host "Resources tagged Project=FHIRBridge currently in resource group '$RgName' (before destroy):"
        try { az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='$RgName']" --output table } catch { Write-Host "  (couldn't query - not logged in to az, or the group doesn't exist)" }
        Write-Host ""
    }

    if ($Yes) {
        terraform destroy -auto-approve
    } else {
        terraform destroy
    }
    # PowerShell does NOT treat a non-zero exit code from a native command (terraform.exe) as a
    # terminating error, even with $ErrorActionPreference = "Stop" - that setting only covers
    # PowerShell's own cmdlets/exceptions. Without this explicit check, the script would print
    # "Done" below even after a partially-failed destroy.
    $DestroyExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($DestroyExitCode -eq 0) {
    Write-Host "Done. Everything this Terraform config created has been removed; the resource group itself was left in place (it wasn't created by this config)."
} else {
    Write-Host "terraform destroy exited with errors (code $DestroyExitCode) - some resources may not have been fully removed. Check the error above; a common one is a Key Vault purge permission error, which is harmless (the vault is still soft-deleted, just not immediately purged)."
}

if ($azAvailable) {
    Write-Host ""
    Write-Host "Resources tagged Project=FHIRBridge remaining in '$RgName' (should be empty):"
    try { az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='$RgName']" --output table } catch { Write-Host "  (couldn't query - not logged in to az)" }
}

exit $DestroyExitCode
