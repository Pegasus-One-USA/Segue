# Scales an already-running azure-deploy (main.bicep) Segue install up or down -- Container App
# CPU/memory sizes, Azure Database for PostgreSQL Flexible Server SKU/storage, and Azure Managed
# Redis tier -- by re-running the same `az deployment group create` with only the size/SKU
# parameters that need to change overridden. Every other parameter keeps whatever value is already
# in -ParametersFile, so this never accidentally resets an unrelated setting.
#
# Only pass the switches you actually want to change; leaving one out keeps its current value.
#
# Usage:
#   .\upgrade-resources.ps1 -ResourceGroup rg-xxx -ParametersFile .\main.parameters.json -SegueAppSize Large
#   .\upgrade-resources.ps1 -ResourceGroup rg-xxx -ParametersFile .\main.parameters.json `
#     -WorkerSize Medium -PostgresSku GeneralPurpose_D2s_v3 -PostgresStorageMb 65536
#   .\upgrade-resources.ps1 ... -WhatIf   # preview only, changes nothing
#
# -SegueAppSize / -WorkerSize / -RedisSize / -PostgresSize   Small | Medium | Large | XLarge
#   (Container App CPU/memory presets -- PostgresSize/RedisSize only apply to the containerized
#   Postgres/Redis path; ignored by main.bicep when the managed path is in use.)
# -PostgresSku    Burstable_B1ms | Burstable_B2s | GeneralPurpose_D2s_v3 | GeneralPurpose_D4s_v3
#   (Azure Database for PostgreSQL Flexible Server compute tier -- only applies when
#   useAzurePostgresql=true in -ParametersFile.)
# -PostgresStorageMb   32768 | 65536 | 131072 | 262144
#   (Flexible Server storage -- Azure only allows scaling this UP, never down; this script checks
#   the server's current size first and refuses a decrease instead of letting the deployment fail.)
# -RedisTier    Balanced_B0 | Balanced_B5 | MemoryOptimized_M10
#   (Azure Managed Redis SKU -- only applies when useAzureCacheForRedis=true in -ParametersFile.)
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$ParametersFile,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$SegueAppSize,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$WorkerSize,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$RedisSize,
    [ValidateSet("Small", "Medium", "Large", "XLarge")][string]$PostgresSize,
    [ValidateSet("Burstable_B1ms", "Burstable_B2s", "GeneralPurpose_D2s_v3", "GeneralPurpose_D4s_v3")][string]$PostgresSku,
    [ValidateSet(32768, 65536, 131072, 262144)][int]$PostgresStorageMb,
    [ValidateSet("Balanced_B0", "Balanced_B5", "MemoryOptimized_M10")][string]$RedisTier,
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

$paramArgs = @()
foreach ($o in $overrides) { $paramArgs += "--parameters"; $paramArgs += $o }

if ($WhatIf) {
    Write-Host "==> Previewing resource change(s) in '$ResourceGroup': $($overrides -join ', ') (no changes applied)" -ForegroundColor Cyan
    az deployment group what-if `
        --resource-group $ResourceGroup `
        --template-file $TemplateFile `
        --parameters "@$ParametersFile" `
        @paramArgs
    if ($LASTEXITCODE -ne 0) { throw "az deployment group what-if failed" }
    exit 0
}

Write-Host "==> Applying resource change(s) in '$ResourceGroup': $($overrides -join ', ')" -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroup `
    --template-file $TemplateFile `
    --parameters "@$ParametersFile" `
    @paramArgs `
    --query "properties.outputs" -o json
if ($LASTEXITCODE -ne 0) { throw "az deployment group create failed" }

Write-Host "Done." -ForegroundColor Green
