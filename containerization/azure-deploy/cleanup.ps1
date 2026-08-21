# Tears down a Bicep (main.bicep) deployment. Unlike the Terraform environments, this path has no
# state file to destroy from — cleanup prefers reading main.bicep's own `resourceManifest` output
# (every resource ID it created, in a dependency-safe deletion order) straight out of the
# deployment's own history — no extra resource needed to store it, Azure keeps deployment outputs
# automatically. This is precise even in a shared resource group, since it only ever offers those
# exact IDs for deletion. Falls back to the resource group being dedicated to this deployment
# (recommended, see README.md's Tier 1 flow), or to the commonTags every taggable resource in
# main.bicep carries (Project/Component/Environment/ManagedBy), only if that manifest isn't
# available — e.g. an older deployment made before this output existed, or its history was purged.
#
# The tag-based mode filters on BOTH Environment=<value> AND ManagedBy=Bicep, not Environment
# alone: a shared resource group can easily also hold a Terraform-managed deployment (../terraform
# environments, ../terraform/vendor-registry) using the SAME Environment tag value (both default to
# "fhirbridge") — those are tagged ManagedBy=Terraform, not Bicep, so this second condition is what
# actually keeps this script from also deleting them.
#
# Interactive selection only applies to the TAG-BASED fallback below, where there's genuine
# ambiguity about what should be included — by default (no -Yes) that list opens in
# Out-GridView (an actual GUI grid window) so you pick exactly which of the listed resources to
# delete (ctrl/shift-click, OK), falling back to a plain numbered text picker if Out-GridView isn't
# available. The MANIFEST path doesn't use a picker at all: it's already a precise, trusted list
# computed directly from main.bicep's own resources, so it's just a plain preview + yes/no confirm.
# -Yes skips any prompt/picker entirely and deletes everything found, for unattended use.
#
# Usage:
#   ./cleanup.ps1 -ResourceGroup fhirbridge-rg -WhatIf   # preview only - prints the checklist, deletes nothing, ever
#   ./cleanup.ps1 -ResourceGroup fhirbridge-rg           # opens the picker on whatever's found
#   ./cleanup.ps1 -EnvironmentTag fhirbridge             # tag-based: picker over Environment=<value> AND ManagedBy=Bicep resources
#   ./cleanup.ps1 -ResourceGroup fhirbridge-rg -DeploymentName main -Yes   # unattended, deletes everything found
param(
    [string]$ResourceGroup = "",
    [string]$DeploymentName = "main",
    [string]$EnvironmentTag = "",
    [switch]$WhatIf,
    [switch]$Yes
)

$ErrorActionPreference = "Stop"

# Shows $Ids in an interactive picker and returns only what the user actually selected. With -Yes,
# returns $Ids unchanged (no picker - unattended mode). Returns $null if the user closes the picker
# without selecting anything (treated as "abort", not "delete nothing but continue").
function Select-ResourcesToDelete {
    param([string[]]$Ids, [switch]$Yes)

    if ($Yes) { return $Ids }

    $gridViewAvailable = [bool](Get-Command Out-GridView -ErrorAction SilentlyContinue)
    if ($gridViewAvailable) {
        Write-Host "Opening the resource picker (Out-GridView) - select the ones to delete, then click OK. Close the window / click Cancel to abort."
        $rows = $Ids | ForEach-Object { [PSCustomObject]@{ ResourceId = $_ } }
        $selected = $rows | Out-GridView -Title "Select resources to delete ($($Ids.Count) found)" -PassThru
        if (-not $selected) { return $null }
        return $selected | ForEach-Object { $_.ResourceId }
    }

    Write-Host "Out-GridView isn't available here - falling back to a text picker."
    for ($i = 0; $i -lt $Ids.Count; $i++) { Write-Host "  [$i] $($Ids[$i])" }
    $reply = Read-Host "Enter numbers to DELETE, comma-separated (e.g. 0,2,3), 'all', or blank to abort"
    if ([string]::IsNullOrWhiteSpace($reply)) { return $null }
    if ($reply.Trim().ToLower() -eq "all") { return $Ids }
    $indices = $reply -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ }
    $picked = $indices | Where-Object { $_ -ge 0 -and $_ -lt $Ids.Count } | ForEach-Object { $Ids[$_] }
    if (-not $picked) { return $null }
    return $picked
}

if ([string]::IsNullOrEmpty($ResourceGroup) -and [string]::IsNullOrEmpty($EnvironmentTag)) {
    Write-Error "Pass either -ResourceGroup <name> (recommended) or -EnvironmentTag <value>."
    exit 1
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "az CLI not found. Install it first (see ../../Documents/Containerization-Multi-Cloud-Guide.html)."
    exit 1
}

if ($ResourceGroup -ne "") {
    Write-Host "==> Looking for deployment '$DeploymentName' in '$ResourceGroup' and its resourceManifest output ..."
    $manifestJson = $null
    try {
        $manifestJson = az deployment group show --resource-group $ResourceGroup --name $DeploymentName --query "properties.outputs.resourceManifest.value" -o json 2>$null
    } catch {
        $manifestJson = $null
    }

    $manifestIds = @()
    if ($manifestJson -and $manifestJson.Trim() -ne "" -and $manifestJson.Trim() -ne "null") {
        try { $manifestIds = $manifestJson | ConvertFrom-Json } catch { $manifestIds = @() }
    }

    # Belt-and-suspenders: never delete a disk snapshot through this script, even if one somehow
    # ended up in a manifest. Snapshots are how backups (e.g. of the pre-existing VM stack sharing
    # this resource group) are protected - this script must never be the thing that deletes them.
    $excludedSnapshots = $manifestIds | Where-Object { $_ -match '/providers/Microsoft\.Compute/snapshots/' }
    if ($excludedSnapshots.Count -gt 0) {
        $manifestIds = $manifestIds | Where-Object { $_ -notmatch '/providers/Microsoft\.Compute/snapshots/' }
        Write-Host "Excluded $($excludedSnapshots.Count) snapshot resource(s) from deletion (snapshots are never deleted by this script):"
        $excludedSnapshots | ForEach-Object { Write-Host "  $_" }
    }

    if ($manifestIds.Count -gt 0) {
        Write-Host "Found a resource manifest with $($manifestIds.Count) resource(s) (deletion-safe order):"
        $manifestIds | ForEach-Object { Write-Host "  $_" }
        if ($WhatIf) {
            Write-Host ""
            Write-Host "-WhatIf: nothing deleted. Re-run without -WhatIf to delete exactly the $($manifestIds.Count) resource(s) listed above."
            exit 0
        }
        if (-not $Yes) {
            $reply = Read-Host "Delete exactly these $($manifestIds.Count) resource(s)? [y/N]"
            if ($reply -notmatch '^[Yy]$') { Write-Host "Aborted."; exit 1 }
        }
        Write-Host "==> az resource delete --ids ... (all $($manifestIds.Count) from the manifest)"
        az resource delete --ids $manifestIds
        Write-Host "Done. $($manifestIds.Count) resource(s) deleted; the resource group itself was left in place."
        exit 0
    }

    Write-Host "No resource manifest found for deployment '$DeploymentName' in '$ResourceGroup' (older deployment, wrong -DeploymentName, or history purged)."

    # Hard safety gate: never fall back to deleting the entire resource group if it contains any
    # disk snapshots - those are backups (see azure-backups/backup-vm-resources.ps1) and this
    # script must never be able to wipe them out, regardless of -Yes/-WhatIf. There is no flag to
    # bypass this - move or delete the snapshot deliberately first if a full group delete is truly
    # intended.
    $existingSnapshots = az resource list -g $ResourceGroup --resource-type Microsoft.Compute/snapshots --query "[].id" -o tsv 2>$null
    if ($existingSnapshots -and $existingSnapshots.Trim() -ne "") {
        Write-Host ""
        Write-Error "Refusing to fall back to 'az group delete' for '$ResourceGroup' - it contains disk snapshot(s), which are backups:"
        ($existingSnapshots -split "`n") | Where-Object { $_.Trim() -ne "" } | ForEach-Object { Write-Host "  $_" }
        Write-Host ""
        Write-Host "Use -EnvironmentTag instead for a precise, tag-scoped cleanup that skips these automatically."
        exit 1
    }

    if ($WhatIf) {
        Write-Host "-WhatIf: no manifest to preview. Falling back would delete the ENTIRE resource group '$ResourceGroup' and everything in it - re-run without -WhatIf only if that's really what you want."
        exit 0
    }
    Write-Host "Falling back to deleting the ENTIRE resource group '$ResourceGroup' and everything in it."
    if (-not $Yes) {
        $reply = Read-Host "Continue? [y/N]"
        if ($reply -notmatch '^[Yy]$') { Write-Host "Aborted."; exit 1 }
    }
    Write-Host "==> az group delete --name $ResourceGroup"
    az group delete --name $ResourceGroup --yes --no-wait
    Write-Host "Deletion started (--no-wait) - check 'az group show --name $ResourceGroup' until it 404s to confirm it's fully gone."
} else {
    Write-Host "==> Finding resources tagged Environment=$EnvironmentTag AND ManagedBy=Bicep ..."
    # az CLI's --tag only accepts one key=value filter at a time, so ManagedBy=Bicep is applied
    # client-side via --query instead of a second --tag flag.
    $ids = az resource list --tag "Environment=$EnvironmentTag" --query "[?tags.ManagedBy=='Bicep'].id" -o tsv

    if (-not $ids) {
        Write-Host "No resources found tagged Environment=$EnvironmentTag and ManagedBy=Bicep. Nothing to do."
        exit 0
    }

    $idList = $ids -split "`n" | Where-Object { $_ -ne "" }

    # Belt-and-suspenders: same snapshot exclusion as the manifest path above - a snapshot would
    # only reach here if someone manually tagged it Environment=.../ManagedBy=Bicep, but this
    # script must never be the thing that deletes a backup snapshot regardless.
    $excludedSnapshots = $idList | Where-Object { $_ -match '/providers/Microsoft\.Compute/snapshots/' }
    if ($excludedSnapshots.Count -gt 0) {
        $idList = $idList | Where-Object { $_ -notmatch '/providers/Microsoft\.Compute/snapshots/' }
        Write-Host "Excluded $($excludedSnapshots.Count) snapshot resource(s) from deletion (snapshots are never deleted by this script):"
        $excludedSnapshots | ForEach-Object { Write-Host "  $_" }
    }

    if ($idList.Count -eq 0) {
        Write-Host "Nothing left to delete after excluding snapshots."
        exit 0
    }

    Write-Host "Found $($idList.Count) resource(s):"
    $idList | ForEach-Object { Write-Host "  $_" }

    if ($WhatIf) {
        Write-Host ""
        Write-Host "-WhatIf: nothing deleted. Re-run without -WhatIf to open the picker over the $($idList.Count) resource(s) listed above."
        exit 0
    }

    $toDelete = Select-ResourcesToDelete -Ids $idList -Yes:$Yes
    if (-not $toDelete) { Write-Host "Aborted - nothing selected."; exit 1 }

    Write-Host "==> az resource delete --ids ... ($($toDelete.Count) of $($idList.Count) selected)"
    az resource delete --ids $toDelete
    Write-Host "Done. $($toDelete.Count) resource(s) deleted."
}
