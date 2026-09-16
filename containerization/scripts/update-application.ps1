# Redeploys an already-running azure-deploy (main.bicep) Segue install onto a new application
# version, with the minimum steps that actually requires: bump imageTag (and optionally rebuild +
# push the images that tag points to) and re-run the SAME `az deployment group create` this
# environment was originally stood up with. That single incremental deployment:
#   - updates code: segue-app/worker Container Apps pick up the new image tag and roll a new
#     revision (Container Apps handles this in place -- no separate "stop/replace" step);
#   - updates the database: no separate migration step is scripted here on purpose -- segue-app
#     and worker both auto-migrate FHIRBridgeDb on boot (see main.bicep's own top-of-file comment
#     and the README's "auto-migrate" note), so the new revision migrates itself as it starts;
#   - updates resources if required: since -ParametersFile is the SAME file (with -ImageTag as the
#     only override this script adds), any other parameter someone already edited in that file
#     (sizes, SKUs, toggles) is applied by this same incremental deployment too -- there's no need
#     for a second command just because more than the image changed.
#
# Keep -ParametersFile pointed at the actual file used for the live deployment (copied from
# main.parameters.example.json, NOT that example itself) so unrelated parameters aren't reset to
# their template defaults.
#
# Usage:
#   .\update-application.ps1 -ResourceGroup rg-xxx -ParametersFile .\main.parameters.json -ImageTag v1.3.0
#   .\update-application.ps1 -ResourceGroup rg-xxx -ParametersFile .\main.parameters.json `
#     -ImageTag v1.3.0 -Registry myregistry.azurecr.io -BuildAndPush
#   .\update-application.ps1 ... -WhatIf   # preview only, changes nothing
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$ParametersFile,
    [Parameter(Mandatory = $true)][string]$ImageTag,
    [string]$Registry = "",
    [switch]$BuildAndPush,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$TemplateFile = Join-Path $ScriptDir "../azure-deploy/main.bicep"

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

if (-not (Test-Path $ParametersFile)) {
    Write-Error "Parameters file not found: $ParametersFile"
    exit 1
}

Assert-AzCli

if ($BuildAndPush) {
    if ($Registry -eq "") {
        Write-Error "-BuildAndPush requires -Registry <registry>"
        exit 1
    }
    Write-Host "==> Building and pushing images tagged '$ImageTag' to $Registry" -ForegroundColor Cyan
    & (Join-Path $ScriptDir "build-images.ps1") -Registry $Registry -Tag $ImageTag -Push
    if ($LASTEXITCODE -ne 0) { throw "build-images.ps1 failed" }
}

if ($WhatIf) {
    Write-Host "==> Previewing update to image tag '$ImageTag' in resource group '$ResourceGroup' (no changes applied)" -ForegroundColor Cyan
    az deployment group what-if `
        --resource-group $ResourceGroup `
        --template-file $TemplateFile `
        --parameters "@$ParametersFile" `
        --parameters imageTag=$ImageTag
    if ($LASTEXITCODE -ne 0) { throw "az deployment group what-if failed" }
    exit 0
}

Write-Host "==> Deploying image tag '$ImageTag' to resource group '$ResourceGroup'" -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroup `
    --template-file $TemplateFile `
    --parameters "@$ParametersFile" `
    --parameters imageTag=$ImageTag `
    --query "properties.outputs" -o json
if ($LASTEXITCODE -ne 0) { throw "az deployment group create failed" }

Write-Host "Done. New revisions of segue-app/worker are rolling out on tag '$ImageTag'; FHIRBridgeDb migrates automatically on boot." -ForegroundColor Green
