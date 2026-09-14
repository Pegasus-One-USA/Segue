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
#   ./cleanup-azure.ps1                     # name prefix read from terraform.tfvars, shows manifest/preview, terraform destroy, then an az-based sweep for anything Terraform's state didn't track
#   ./cleanup-azure.ps1 -NamePrefix segue8  # explicit override - drives the SAFETY GATE below AND the orphan sweep at the end (see their own comments)
#   ./cleanup-azure.ps1 -Yes                # skip both confirmation prompts (terraform destroy AND the sweep)
param(
    [switch]$Yes,
    # Filters the fallback preview to resources actually named "<this>*", AND - more importantly -
    # is checked against every resource this state actually tracks before destroy runs at all: if
    # ANY of them has a real Azure name that doesn't start with this prefix, the script aborts
    # instead of destroying. Added after a real incident where a completely separate, persistent
    # resource (the vendor registry ACR, terraform/vendor-registry - meant to outlive every
    # disposable test deployment here) ended up destroyed alongside a test environment's own
    # resources. Defaults to terraform.tfvars' own name_prefix value if not passed explicitly.
    [string]$NamePrefix = ""
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

    # Read the target resource group name + name_prefix from tfvars (both fall back to a default) -
    # RgName is purely for the tag-filtered fallback preview below (not used by `terraform destroy`
    # itself, which is state-scoped); NamePrefix additionally drives the safety gate further down.
    $RgName = "rg-tusharpuri"
    $TfNamePrefix = "fhirbridge"
    if (Test-Path "terraform.tfvars") {
        $match = Select-String -Path "terraform.tfvars" -Pattern '^\s*resource_group_name\s*=\s*"([^"]*)"' | Select-Object -First 1
        if ($match) { $RgName = $match.Matches[0].Groups[1].Value }
        $prefixMatch = Select-String -Path "terraform.tfvars" -Pattern '^\s*name_prefix\s*=\s*"([^"]*)"' | Select-Object -First 1
        if ($prefixMatch) { $TfNamePrefix = $prefixMatch.Matches[0].Groups[1].Value }
    }
    if ($NamePrefix -eq "") { $NamePrefix = $TfNamePrefix }
    Write-Host "Using name prefix '$NamePrefix' as the preview filter and pre-destroy safety gate (pass -NamePrefix to override)."

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
            Write-Host "Resources tagged Project=FHIRBridge AND named '$NamePrefix*' in resource group '$RgName' (before destroy):"
            try { az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='$RgName' && starts_with(name, '$NamePrefix')]" --output table } catch { Write-Host "  (couldn't query - not logged in to az, or the group doesn't exist)" }
            try {
                $foreign = az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='$RgName' && !starts_with(name, '$NamePrefix')]" --output table 2>$null
                if ($foreign) {
                    Write-Host ""
                    Write-Host "NOTE: other Project=FHIRBridge resources exist in '$RgName' but do NOT match prefix '$NamePrefix' - left untouched (e.g. the persistent vendor registry):"
                    Write-Host $foreign
                }
            } catch {}
            Write-Host ""
        }
    }

    # --- Safety gate: refuse to destroy if this state tracks anything outside NamePrefix ---
    #
    # terraform destroy is state-scoped, not tag- or prefix-scoped - the preview above is purely
    # informational. This is the check that actually prevents a repeat of the vendor-registry
    # incident: it inspects every resource THIS state really tracks (via `terraform show -json`,
    # the same source of truth destroy itself uses) and aborts before destroy runs at all if any of
    # them has a real Azure name that doesn't start with $NamePrefix - e.g. a resource that got
    # imported here by mistake. Resources without a plain "name" attribute (access policies keyed
    # by object_id, storage blobs, data sources, random_id, etc.) aren't a naming-collision risk and
    # are skipped.
    Write-Host "==> Verifying every resource in this state matches name prefix '$NamePrefix' before destroying ..."
    $mismatches = @()
    try {
        $stateJson = terraform show -json 2>$null | ConvertFrom-Json
        if ($stateJson.values.root_module.resources) {
            foreach ($res in $stateJson.values.root_module.resources) {
                if ($res.mode -ne "managed") { continue }
                $resName = $res.values.name
                if (-not $resName) { continue }
                if (-not $resName.StartsWith($NamePrefix)) {
                    $mismatches += "$($res.address) -> real Azure name '$resName' (does not start with '$NamePrefix')"
                }
            }
        }
    } catch {
        Write-Host "  (couldn't parse 'terraform show -json' - skipping the safety gate; review the preview above carefully before confirming)"
    }

    if ($mismatches.Count -gt 0) {
        Write-Host ""
        Write-Host "ABORTING - this Terraform state tracks a resource that does NOT match name prefix '$NamePrefix':" -ForegroundColor Red
        $mismatches | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        Write-Host ""
        Write-Host "This usually means a resource ended up in this state by mistake (e.g. the persistent vendor registry got imported/created here). Investigate with 'terraform state list' / 'terraform state show <address>', remove it from THIS state with 'terraform state rm <address>' if it doesn't belong here (that only detaches it from tracking - it does NOT delete the real resource), then re-run this script."
        exit 1
    }
    Write-Host "OK - every resource in state matches prefix '$NamePrefix'."

    if (-not $Yes) {
        $reply = Read-Host "Destroy everything shown above? [y/N]"
        if ($reply -notmatch '^[Yy]$') { Write-Host "Aborted."; exit 1 }
    }

    # destroy still needs a value for every required variable to evaluate the config graph (same
    # as apply), but never actually USES them - nothing is created or updated on the way to
    # deleting everything. Rather than let Terraform interactively prompt for real secrets just to
    # tear things down, backfill a throwaway placeholder for any required variable terraform.tfvars
    # doesn't already define - values that ARE in tfvars are left alone and used as-is.
    $RequiredVars = @("postgres_password", "jwt_signing_key", "redis_password")
    $DestroyVarArgs = @()
    foreach ($v in $RequiredVars) {
        $has = (Test-Path "terraform.tfvars") -and [bool](Select-String -Path "terraform.tfvars" -Pattern "^\s*$v\s*=" -Quiet)
        if (-not $has) {
            $DestroyVarArgs += "-var"
            $DestroyVarArgs += "$v=destroy-only-placeholder"
        }
    }
    if ($DestroyVarArgs.Count -gt 0) {
        Write-Host "terraform.tfvars doesn't define every required variable - supplying throwaway placeholder values for this destroy only (safe: destroy never uses them to create or update anything)."
    }

    # -auto-approve here regardless of $Yes: the confirmation gate above already covers it (unless
    # -Yes was passed to skip that too), so Terraform's own separate "type yes" prompt would just
    # be a redundant second confirmation of the same action.
    terraform destroy -auto-approve @DestroyVarArgs
    # PowerShell does NOT treat a non-zero exit code from a native command (terraform.exe) as a
    # terminating error, even with $ErrorActionPreference = "Stop" - that setting only covers
    # PowerShell's own cmdlets/exceptions. Without this explicit check, the script would print
    # "Done" below even after a partially-failed destroy.
    $DestroyExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($DestroyExitCode -eq 0) {
    Write-Host "Done. Everything this Terraform config's OWN STATE tracked has been removed; the resource group itself was left in place (it wasn't created by this config)."
} else {
    Write-Host "terraform destroy exited with errors (code $DestroyExitCode) - some resources may not have been fully removed. Check the error above; a common one is a Key Vault purge permission error, which is harmless (the vault is still soft-deleted, just not immediately purged)."
}

# --- Orphan sweep: catch resources this state doesn't track but still match $NamePrefix ---
#
# `terraform destroy` above is entirely state-scoped - it reports "0 to destroy" for a deployment
# that was applied from a different machine/directory (its state never made it here), even though
# the real Azure resources are still sitting in the resource group. This sweep catches that case:
# it looks for anything named "$NamePrefix*" directly in the resource group (NOT a Project=
# FHIRBridge tag match - this resource group is known to hold unrelated infrastructure alongside
# these deployments, e.g. a dev VM, healthcare API workspaces, other storage accounts, even an
# unrelated second Container Apps deployment - a tag alone isn't tight enough scoping here), shows
# exactly what it found, and asks before deleting. Deletes in dependency-safe batches (managed
# certificates, then Container Apps, then the Container Apps Environment they ran in, then whatever
# is left) rather than one bulk call, since ARM doesn't guarantee ordering across an arbitrary
# --ids list.
if ($azAvailable) {
    Write-Host ""
    Write-Host "==> Sweeping for any remaining resources named '$NamePrefix*' in '$RgName' (catches deployments this state doesn't track) ..."
    $remainingJson = az resource list --resource-group $RgName --query "[?starts_with(name, '$NamePrefix')]" -o json 2>$null
    $remaining = @()
    if ($remainingJson) { $remaining = $remainingJson | ConvertFrom-Json }

    if ($remaining.Count -gt 0) {
        Write-Host "Found $($remaining.Count) resource(s) matching '$NamePrefix*' still in '$RgName':"
        $remaining | ForEach-Object { Write-Host "  - $($_.name) ($($_.type))" }

        $doSweep = $Yes
        if (-not $Yes) {
            $reply2 = Read-Host "Delete these via 'az resource delete' (separate from the Terraform destroy above)? [y/N]"
            $doSweep = $reply2 -match '^[Yy]$'
        }

        if ($doSweep) {
            # Managed certificates FIRST, then apps, then the environment. Confirmed empirically
            # (2026-08-28, segue10 cleanup): deleting an app that still has a bound managed
            # certificate is SLOW (~20 min for one app, via `az containerapp delete`) and can even
            # get stuck in a broken half-deleted state (ingress stripped but provisioningState
            # stuck at "Failed" forever - recovered only by a generic `az resource delete --ids`
            # against the same resource, which is exactly what this sweep already uses). Deleting
            # the certificate FIRST removes that entanglement up front - a test app whose pending
            # cert was deleted first took 3 seconds to delete, vs. 20+ minutes for one whose
            # succeeded cert was still attached. If a cert delete fails here (e.g. a live
            # SniEnabled binding that genuinely can't be dropped that easily), it's non-fatal - the
            # subsequent app delete just falls back to the slower path, same as before this fix.
            # The environment goes last since it's the parent of both.
            $typeOrder = @(
                "Microsoft.App/managedEnvironments/managedCertificates",
                "Microsoft.App/containerApps",
                "Microsoft.App/managedEnvironments"
            )
            $handledIds = @{}
            foreach ($t in $typeOrder) {
                # @(...) forces array context - Where-Object returns a bare (non-array) object for
                # exactly one match, and .Count on that is $null, not 1.
                $batch = @($remaining | Where-Object { $_.type -eq $t })
                if ($batch.Count -gt 0) {
                    Write-Host "  Deleting $($batch.Count) resource(s) of type $t ..."
                    az resource delete --ids ($batch | ForEach-Object { $_.id })
                    $batch | ForEach-Object { $handledIds[$_.id] = $true }
                }
            }
            $rest = @($remaining | Where-Object { -not $handledIds.ContainsKey($_.id) })
            if ($rest.Count -gt 0) {
                Write-Host "  Deleting $($rest.Count) remaining resource(s) ..."
                az resource delete --ids ($rest | ForEach-Object { $_.id })
            }
            Write-Host "Sweep complete. If any resource above still failed to delete, re-run this script - a common cause is a Container Apps Environment deletion (slow, several minutes) that hadn't finished cascading yet."
        } else {
            Write-Host "Skipped - left as-is."
        }
    } else {
        Write-Host "None found - nothing to sweep."
    }

    Write-Host ""
    Write-Host "Resources named '$NamePrefix*' remaining in '$RgName' (should be empty):"
    try { az resource list --resource-group $RgName --query "[?starts_with(name, '$NamePrefix')]" --output table } catch { Write-Host "  (couldn't query - not logged in to az)" }
}

exit $DestroyExitCode
