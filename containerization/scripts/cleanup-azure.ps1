# Tears down everything the Azure Terraform environment created inside its target resource group -
# ACR, Log Analytics workspace, Container Apps Environment, Key Vault, storage account/shares, and
# all 5 Container Apps. The resource group itself is a pre-existing one this config only deploys
# INTO (see the data "azurerm_resource_group" "main" block in main.tf) - `terraform destroy` removes
# everything Terraform created but leaves the resource group itself intact, since it wasn't created
# by this config and may be shared with other things.
#
# Before destroying, this downloads and shows main.tf's own resource-manifest blob
# (resources.txt, in the storage account this config creates) as the preview - not a Terraform
# state query, an actual file that survives independently of state. It's downloaded BEFORE
# `terraform destroy` runs, while the blob (and everything else) still exists. Since that manifest
# is computed directly from Terraform's own resource references, it's already precise - no
# selection step needed here, just a clear preview and a plain confirm. Falls back to the
# tag-filtered `az resource list` preview only if the manifest can't be fetched (e.g. a very old
# deployment made before this output existed).
#
# The actual deletion still goes through `terraform destroy`, not manual `az resource delete` calls
# - that's what correctly handles dependency order AND keeps Terraform's state in sync afterward.
#
# Usage:
#   ./cleanup-azure.ps1          # shows the manifest (or tag-based fallback), then terraform destroy
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
    # purely for the tag-filtered fallback preview below, not used by `terraform destroy` itself
    # (that's state-scoped and never touches anything outside what Terraform created, tagged or
    # not).
    $RgName = "rg-tusharpuri"
    if (Test-Path "terraform.tfvars") {
        $match = Select-String -Path "terraform.tfvars" -Pattern '^\s*resource_group_name\s*=\s*"([^"]*)"' | Select-Object -First 1
        if ($match) { $RgName = $match.Matches[0].Groups[1].Value }
    }

    $azAvailable = [bool](Get-Command az -ErrorAction SilentlyContinue)
    $manifestShown = $false

    if ($azAvailable) {
        Write-Host "==> Fetching the resource manifest (resources.txt) before anything is destroyed ..."
        $manifestLocalFile = Join-Path $env:TEMP "fhirbridge-resources-preview.txt"
        try {
            $downloadCmd = terraform output -raw resource_manifest_download_cmd 2>$null
            if ($downloadCmd -and $downloadCmd.Trim() -ne "") {
                $cmdWithLocalPath = $downloadCmd -replace '--file\s+\S+', "--file `"$manifestLocalFile`""
                Invoke-Expression "$cmdWithLocalPath --only-show-errors" *> $null
                if (Test-Path $manifestLocalFile) {
                    Write-Host ""
                    Write-Host "----- resources.txt (fetched just now, before destroy) -----"
                    Get-Content $manifestLocalFile | ForEach-Object { Write-Host $_ }
                    Write-Host "--------------------------------------------------------------"
                    Write-Host ""
                    $manifestShown = $true
                    Remove-Item $manifestLocalFile -ErrorAction SilentlyContinue
                }
            }
        } catch {
            $manifestShown = $false
        }

        if (-not $manifestShown) {
            Write-Host "Couldn't fetch resources.txt (older deployment, or not logged in to az) - falling back to a tag-based preview instead."
            Write-Host "Resources tagged Project=FHIRBridge currently in resource group '$RgName' (before destroy):"
            try { az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='$RgName']" --output table } catch { Write-Host "  (couldn't query - not logged in to az, or the group doesn't exist)" }
            Write-Host ""
        }
    }

    if (-not $Yes) {
        $reply = Read-Host "Destroy everything shown above? [y/N]"
        if ($reply -notmatch '^[Yy]$') { Write-Host "Aborted."; exit 1 }
    }

    # -auto-approve here regardless of $Yes: the confirmation gate above already covers it (unless
    # -Yes was passed to skip that too), so Terraform's own separate "type yes" prompt would just
    # be a redundant second confirmation of the same action.
    terraform destroy -auto-approve
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
