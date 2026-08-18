# Tears down the persistent vendor Container Registry (containerization/terraform/vendor-registry)
# - a single ACR that holds published, compiled release images, kept separate from any per-client
# test deployment. The resource group itself is a pre-existing one this config only deploys INTO
# (see the data "azurerm_resource_group" "main" block in main.tf) - `terraform destroy` removes
# the registry but leaves the resource group itself intact, since it wasn't created by this config
# and (today) is shared with the ../environments/azure test deployment too.
#
# Usage:
#   ./cleanup-vendor-registry.ps1          # prompts for confirmation, then terraform destroy
#   ./cleanup-vendor-registry.ps1 -Yes     # skip the confirmation prompt (-auto-approve)
param(
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$EnvDir = Join-Path $RepoRoot "containerization/terraform/vendor-registry"

Push-Location $EnvDir
try {
    if (-not (Test-Path "terraform.tfstate") -and -not (Test-Path ".terraform")) {
        Write-Error "No local Terraform state found in $EnvDir - nothing to destroy from here. (If state is stored remotely, run 'terraform init' first.)"
        exit 1
    }

    # Read the target resource group from tfvars (falls back to the variable default) - purely for
    # the tag-filtered preview below, not used by `terraform destroy` itself (that's state-scoped
    # and never touches anything outside what Terraform created, tagged or not).
    $RgName = "rg-tusharpuri"
    if (Test-Path "terraform.tfvars") {
        $match = Select-String -Path "terraform.tfvars" -Pattern '^\s*resource_group_name\s*=\s*"([^"]*)"' | Select-Object -First 1
        if ($match) { $RgName = $match.Matches[0].Groups[1].Value }
    }

    # Filters on BOTH Project and Component tags (not just Project), since this resource group may
    # currently also hold an unrelated ../environments/azure test deployment tagged
    # Component=containerization - without the Component filter, this preview would show both and
    # make it unclear whether cleanup actually worked.
    $azAvailable = [bool](Get-Command az -ErrorAction SilentlyContinue)
    if ($azAvailable) {
        Write-Host "Vendor-registry resources currently in '$RgName' (before destroy):"
        try { az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='$RgName' && tags.Component=='vendor-registry']" --output table } catch { Write-Host "  (couldn't query - not logged in to az, or the group doesn't exist)" }
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
    Write-Host "Done. The vendor registry has been removed; the resource group itself was left in place (it wasn't created by this config)."
} else {
    Write-Host "terraform destroy exited with errors (code $DestroyExitCode) - the registry may not have been fully removed. Check the error above."
}

if ($azAvailable) {
    Write-Host ""
    Write-Host "Vendor-registry resources remaining in '$RgName' (should be empty):"
    try { az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='$RgName' && tags.Component=='vendor-registry']" --output table } catch { Write-Host "  (couldn't query - not logged in to az)" }
}

exit $DestroyExitCode
