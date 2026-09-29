# Redeploys an already-running azure-deploy (main.bicep) Segue install onto a new application
# version, with the minimum steps that actually requires: bump imageTag (and optionally rebuild +
# push the images that tag points to) and re-run `az deployment group create` reusing every other
# setting exactly as it was last deployed. Two ways to supply "everything else":
#   - Manifest mode (default, no -ParametersFile): reads main.bicep's own `deploymentManifest`
#     output back from the resource group's deployment history (az deployment group show --name
#     main) -- namePrefix, Postgres/Redis mode, sizes, SKUs, and every other non-secret setting come
#     from there automatically, so this deployment can't silently drift back to main.bicep's
#     defaults. Only the 3 things Azure never returns to anything, ever -- Postgres password, JWT
#     signing key, and (if Redis is containerized) Redis password/cert thumbprint -- must still be
#     passed explicitly every time.
#   - Legacy file mode (-ParametersFile given): reuses a saved parameters JSON file wholesale, same
#     as this script's original design -- the fallback for an install predating this manifest output
#     or whose deployment history was purged.
# Either way this single incremental deployment:
#   - updates code: segue-app/worker Container Apps pick up the new image tag and roll a new
#     revision (Container Apps handles this in place -- no separate "stop/replace" step);
#   - updates the database: no separate migration step is scripted here on purpose -- segue-app
#     and worker both auto-migrate FHIRBridgeDb on boot (see main.bicep's own top-of-file comment
#     and the README's "auto-migrate" note), so the new revision migrates itself as it starts;
#   - updates resources if required: every other setting is reapplied exactly as it already was, so
#     nothing resets to main.bicep's template defaults just because this ran.
#
# Usage:
#   .\update-application.ps1 -ResourceGroup rg-xxx -ImageTag v1.3.0 `
#     -PostgresPassword '...' -JwtSigningKey '...'
#   .\update-application.ps1 -ResourceGroup rg-xxx -ImageTag v1.3.0 `
#     -PostgresPassword '...' -JwtSigningKey '...' -RedisPassword '...' -RedisTrustedCertificateThumbprint '...'
#   .\update-application.ps1 -ResourceGroup rg-xxx -ParametersFile .\main.parameters.json -ImageTag v1.3.0   # legacy file mode
#   .\update-application.ps1 ... -Registry myregistry.azurecr.io -BuildAndPush
#   .\update-application.ps1 ... -WhatIf   # preview only, changes nothing
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$ImageTag,
    [string]$PostgresPassword = "",
    [string]$JwtSigningKey = "",
    [string]$RedisPassword = "",
    [string]$RedisTrustedCertificateThumbprint = "",
    [string]$ImageRegistryUsername = "",
    [string]$ImageRegistryPassword = "",
    [string]$ParametersFile = "",
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

function ConvertTo-ArmBool([bool]$Value) {
    if ($Value) { return "true" } else { return "false" }
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

$paramValues = @()

if ($ParametersFile -ne "") {
    if (-not (Test-Path $ParametersFile)) {
        Write-Error "Parameters file not found: $ParametersFile"
        exit 1
    }
    Write-Host "==> Legacy file mode: reusing $ParametersFile" -ForegroundColor Cyan
    $paramValues += "@$ParametersFile"
} else {
    Write-Host "==> Manifest mode: reading prior deployment settings from resource group '$ResourceGroup'" -ForegroundColor Cyan
    $manifestJson = az deployment group show --resource-group $ResourceGroup --name main `
        --query "properties.outputs.deploymentManifest.value" -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($manifestJson) -or $manifestJson.Trim() -eq "null") {
        Write-Error "No prior deployment manifest found (az deployment group show --name main). Either this resource group has no main.bicep deployment yet, its deployment history was purged, or it predates this manifest. Re-run with -ParametersFile pointing at your original parameters file, or use the full Deploy-to-Azure wizard instead."
        exit 1
    }
    $manifest = $manifestJson | ConvertFrom-Json

    if ($PostgresPassword -eq "" -or $JwtSigningKey -eq "") {
        Write-Error "Manifest mode requires -PostgresPassword and -JwtSigningKey (the same values your original install used) -- Azure never returns secret parameter values, so these can't be read back automatically."
        exit 1
    }
    if (-not $manifest.useAzureCacheForRedis -and ($RedisPassword -eq "" -or $RedisTrustedCertificateThumbprint -eq "")) {
        Write-Error "This install's Redis is Containerized (per the deployment manifest) -- -RedisPassword and -RedisTrustedCertificateThumbprint are required (the same values your original install used)."
        exit 1
    }

    $paramValues += "namePrefix=$($manifest.namePrefix)"
    $paramValues += "location=$($manifest.location)"
    $paramValues += "useAzurePostgresql=$(ConvertTo-ArmBool $manifest.useAzurePostgresql)"
    $paramValues += "azurePostgresqlSku=$($manifest.azurePostgresqlSku)"
    $paramValues += "azurePostgresqlStorageMb=$($manifest.azurePostgresqlStorageMb)"
    $paramValues += "postgresSize=$($manifest.postgresSize)"
    $paramValues += "useAzureCacheForRedis=$(ConvertTo-ArmBool $manifest.useAzureCacheForRedis)"
    $paramValues += "azureCacheForRedisTier=$($manifest.azureCacheForRedisTier)"
    $paramValues += "redisSize=$($manifest.redisSize)"
    $paramValues += "segueAppSize=$($manifest.segueAppSize)"
    $paramValues += "workerSize=$($manifest.workerSize)"
    $paramValues += "enableTenantSecretsKeyVault=$(ConvertTo-ArmBool $manifest.enableTenantSecretsKeyVault)"
    $paramValues += "storageRedundancy=$($manifest.storageRedundancy)"
    $paramValues += "enableSeq=$(ConvertTo-ArmBool $manifest.enableSeq)"
    $paramValues += "seqSize=$($manifest.seqSize)"
    $paramValues += "enableFrontDoorWaf=$(ConvertTo-ArmBool $manifest.enableFrontDoorWaf)"
    $paramValues += "wafPolicyMode=$($manifest.wafPolicyMode)"
    $paramValues += "imageRegistryServer=$($manifest.imageRegistryServer)"
    $paramValues += "postgresPassword=$PostgresPassword"
    $paramValues += "jwtSigningKey=$JwtSigningKey"
    if (-not $manifest.useAzureCacheForRedis) {
        $paramValues += "redisPassword=$RedisPassword"
        $paramValues += "redisTrustedCertificateThumbprint=$RedisTrustedCertificateThumbprint"
    }
    if ($ImageRegistryUsername -ne "") { $paramValues += "imageRegistryUsername=$ImageRegistryUsername" }
    if ($ImageRegistryPassword -ne "") { $paramValues += "imageRegistryPassword=$ImageRegistryPassword" }
}

$paramValues += "imageTag=$ImageTag"

$azParamArgs = @()
foreach ($p in $paramValues) { $azParamArgs += @("--parameters", $p) }

if ($WhatIf) {
    Write-Host "==> Previewing update to image tag '$ImageTag' in resource group '$ResourceGroup' (no changes applied)" -ForegroundColor Cyan
    az deployment group what-if `
        --resource-group $ResourceGroup `
        --template-file $TemplateFile `
        @azParamArgs
    if ($LASTEXITCODE -ne 0) { throw "az deployment group what-if failed" }
    exit 0
}

Write-Host "==> Deploying image tag '$ImageTag' to resource group '$ResourceGroup'" -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroup `
    --template-file $TemplateFile `
    @azParamArgs `
    --query "properties.outputs" -o json
if ($LASTEXITCODE -ne 0) { throw "az deployment group create failed" }

Write-Host "Done. New revisions of segue-app/worker are rolling out on tag '$ImageTag'; FHIRBridgeDb migrates automatically on boot." -ForegroundColor Green
