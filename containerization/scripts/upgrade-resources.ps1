# Scales an already-running azure-deploy (main.bicep) Segue install up or down -- Container App
# CPU/memory sizes, Azure Database for PostgreSQL Flexible Server SKU/storage, and Azure Managed
# Redis tier -- by re-running `az deployment group create` with only the size/SKU parameters that
# need to change overridden. Two ways to supply "everything else":
#   - Manifest mode (default, no -ParametersFile): reads main.bicep's own `deploymentManifest`
#     output back from the resource group's deployment history (az deployment group show --name
#     main) -- namePrefix, Postgres/Redis mode, current sizes, image tag, and every other non-secret
#     setting come from there automatically, then your -SegueAppSize/-PostgresSku/etc. overrides are
#     applied on top. Only the 3 things Azure never returns to anything, ever -- Postgres password,
#     JWT signing key, and (if Redis is containerized) Redis password/cert thumbprint -- must still
#     be passed explicitly every time.
#   - Legacy file mode (-ParametersFile given): reuses a saved parameters JSON file wholesale, same
#     as this script's original design -- the fallback for an install predating this manifest output
#     or whose deployment history was purged.
#
# Only pass the switches for what you actually want to change; leaving one out keeps its current
# value (from the manifest, or from -ParametersFile in legacy mode).
#
# Usage:
#   .\upgrade-resources.ps1 -ResourceGroup rg-xxx -SegueAppSize Large `
#     -PostgresPassword '...' -JwtSigningKey '...'
#   .\upgrade-resources.ps1 -ResourceGroup rg-xxx -WorkerSize Medium -PostgresSku GeneralPurpose_D2s_v3 `
#     -PostgresStorageMb 65536 -PostgresPassword '...' -JwtSigningKey '...'
#   .\upgrade-resources.ps1 -ResourceGroup rg-xxx -ParametersFile .\main.parameters.json -SegueAppSize Large   # legacy file mode
#   .\upgrade-resources.ps1 ... -WhatIf   # preview only, changes nothing
#
# -SegueAppSize / -WorkerSize / -RedisSize / -PostgresSize   Small | Medium | Large | XLarge
#   (Container App CPU/memory presets -- PostgresSize/RedisSize only apply to the containerized
#   Postgres/Redis path; ignored by main.bicep when the managed path is in use.)
# -PostgresSku    Burstable_B1ms | Burstable_B2s | GeneralPurpose_D2s_v3 | GeneralPurpose_D4s_v3
#   (Azure Database for PostgreSQL Flexible Server compute tier -- only applies when
#   useAzurePostgresql=true.)
# -PostgresStorageMb   32768 | 65536 | 131072 | 262144
#   (Flexible Server storage -- Azure only allows scaling this UP, never down; this script checks
#   the server's current size first and refuses a decrease instead of letting the deployment fail.)
# -RedisTier    Balanced_B0 | Balanced_B5 | MemoryOptimized_M10
#   (Azure Managed Redis SKU -- only applies when useAzureCacheForRedis=true.)
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$SegueAppSize,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$WorkerSize,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$RedisSize,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$PostgresSize,
    [ValidateSet("Burstable_B1ms", "Burstable_B2s", "GeneralPurpose_D2s_v3", "GeneralPurpose_D4s_v3")][string]$PostgresSku,
    [ValidateSet(32768, 65536, 131072, 262144)][int]$PostgresStorageMb,
    [ValidateSet("Balanced_B0", "Balanced_B5", "MemoryOptimized_M10")][string]$RedisTier,
    [string]$PostgresPassword = "",
    [string]$JwtSigningKey = "",
    [string]$RedisPassword = "",
    [string]$RedisTrustedCertificateThumbprint = "",
    [string]$ImageRegistryUsername = "",
    [string]$ImageRegistryPassword = "",
    [string]$ParametersFile = "",
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

$overrides = @()
if ($SegueAppSize)    { $overrides += "segueAppSize=$SegueAppSize" }
if ($WorkerSize)      { $overrides += "workerSize=$WorkerSize" }
if ($RedisSize)       { $overrides += "redisSize=$RedisSize" }
if ($PostgresSize)    { $overrides += "postgresSize=$PostgresSize" }
if ($PostgresSku)     { $overrides += "azurePostgresqlSku=$PostgresSku" }
if ($PSBoundParameters.ContainsKey("PostgresStorageMb")) { $overrides += "azurePostgresqlStorageMb=$PostgresStorageMb" }
if ($RedisTier)       { $overrides += "azureCacheForRedisTier=$RedisTier" }

if ($overrides.Count -eq 0) {
    Write-Error "No size/SKU changes given -- pass at least one of -SegueAppSize/-WorkerSize/-RedisSize/-PostgresSize/-PostgresSku/-PostgresStorageMb/-RedisTier."
    exit 1
}

Assert-AzCli

if ($PSBoundParameters.ContainsKey("PostgresStorageMb")) {
    # Azure Database for PostgreSQL Flexible Server storage can only be scaled up, never down --
    # catch a decrease here with a clear message instead of letting the deployment fail partway.
    $currentGb = az postgres flexible-server list --resource-group $ResourceGroup `
        --query "[?ends_with(name, '-pg')].storage.storageSizeGb | [0]" -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($currentGb)) {
        $requestedGb = $PostgresStorageMb / 1024
        if ($requestedGb -lt [int]$currentGb) {
            Write-Error "Requested Postgres storage (${requestedGb}GB) is smaller than the current size (${currentGb}GB) -- Azure does not support shrinking Flexible Server storage."
            exit 1
        }
    }
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
    $paramValues += "imageTag=$($manifest.imageTag)"
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

# Overrides listed AFTER the manifest/file baseline -- az CLI applies later --parameters values on
# top of earlier ones when the same key repeats, so these win over whatever the baseline set.
$paramValues += $overrides

$azParamArgs = @()
foreach ($p in $paramValues) { $azParamArgs += @("--parameters", $p) }

if ($WhatIf) {
    Write-Host "==> Previewing resource change(s) in '$ResourceGroup': $($overrides -join ', ') (no changes applied)" -ForegroundColor Cyan
    az deployment group what-if `
        --resource-group $ResourceGroup `
        --template-file $TemplateFile `
        @azParamArgs
    if ($LASTEXITCODE -ne 0) { throw "az deployment group what-if failed" }
    exit 0
}

Write-Host "==> Applying resource change(s) in '$ResourceGroup': $($overrides -join ', ')" -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroup `
    --template-file $TemplateFile `
    @azParamArgs `
    --query "properties.outputs" -o json
if ($LASTEXITCODE -ne 0) { throw "az deployment group create failed" }

Write-Host "Done." -ForegroundColor Green
