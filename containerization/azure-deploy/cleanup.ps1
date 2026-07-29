# Tears down a Bicep (main.bicep) deployment. Unlike the Terraform environments, this path has no
# state file to destroy from — cleanup instead relies on the resource group being dedicated to
# this deployment (recommended, see README.md's Tier 1 flow), or on the commonTags every taggable
# resource in main.bicep carries (Project/Component/Environment/ManagedBy) if you deployed into a
# shared resource group instead.
#
# Usage:
#   ./cleanup.ps1 -ResourceGroup fhirbridge-rg   # delete the whole resource group (recommended)
#   ./cleanup.ps1 -EnvironmentTag fhirbridge     # tag-based: delete only Environment=<value>-tagged resources
#   ./cleanup.ps1 -ResourceGroup fhirbridge-rg -Yes
param(
    [string]$ResourceGroup = "",
    [string]$EnvironmentTag = "",
    [switch]$Yes
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($ResourceGroup) -and [string]::IsNullOrEmpty($EnvironmentTag)) {
    Write-Error "Pass either -ResourceGroup <name> (recommended) or -EnvironmentTag <value>."
    exit 1
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "az CLI not found. Install it first (see ../../Documents/Containerization-Multi-Cloud-Guide.html)."
    exit 1
}

if ($ResourceGroup -ne "") {
    Write-Host "This will delete the ENTIRE resource group '$ResourceGroup' and everything in it."
    if (-not $Yes) {
        $reply = Read-Host "Continue? [y/N]"
        if ($reply -notmatch '^[Yy]$') { Write-Host "Aborted."; exit 1 }
    }
    Write-Host "==> az group delete --name $ResourceGroup"
    az group delete --name $ResourceGroup --yes --no-wait
    Write-Host "Deletion started (--no-wait) - check 'az group show --name $ResourceGroup' until it 404s to confirm it's fully gone."
} else {
    Write-Host "==> Finding resources tagged Environment=$EnvironmentTag ..."
    $ids = az resource list --tag "Environment=$EnvironmentTag" --query "[].id" -o tsv

    if (-not $ids) {
        Write-Host "No resources found tagged Environment=$EnvironmentTag. Nothing to do."
        exit 0
    }

    $idList = $ids -split "`n" | Where-Object { $_ -ne "" }
    Write-Host "Found $($idList.Count) resource(s):"
    $idList | ForEach-Object { Write-Host "  $_" }

    if (-not $Yes) {
        $reply = Read-Host "Delete all $($idList.Count) of these? [y/N]"
        if ($reply -notmatch '^[Yy]$') { Write-Host "Aborted."; exit 1 }
    }

    Write-Host "==> az resource delete --ids ..."
    az resource delete --ids $idList
    Write-Host "Done. $($idList.Count) tagged resource(s) deleted."
}
