#!/usr/bin/env bash
# Same behaviour as upgrade-resources.ps1 -- see that file's header for the full flag reference
# (Container App CPU/memory presets, Postgres Flexible Server SKU/storage, Azure Managed Redis
# tier). Only pass the flags for what actually needs to change; everything else keeps whatever
# value is already in -f's parameters file.
#
# Usage:
#   ./upgrade-resources.sh -g rg-xxx -f ./main.parameters.json -a Large
#   ./upgrade-resources.sh -g rg-xxx -f ./main.parameters.json -w Medium -k GeneralPurpose_D2s_v3 -m 65536
#   ./upgrade-resources.sh ... -x   # preview only, changes nothing
#
# -a SEGUE_APP_SIZE   Small|Medium|Large|XLarge
# -w WORKER_SIZE      Small|Medium|Large|XLarge
# -e REDIS_SIZE       Small|Medium|Large|XLarge   (containerized Redis path only)
# -o POSTGRES_SIZE    Small|Medium|Large|XLarge   (containerized Postgres path only)
# -k POSTGRES_SKU     Burstable_B1ms|Burstable_B2s|GeneralPurpose_D2s_v3|GeneralPurpose_D4s_v3
# -m POSTGRES_STORAGE_MB   32768|65536|131072|262144 (up only -- Azure can't shrink Flexible Server storage)
# -c REDIS_TIER       Balanced_B0|Balanced_B5|MemoryOptimized_M10   (managed Redis path only)
set -euo pipefail

RESOURCE_GROUP=""
PARAMETERS_FILE=""
SEGUE_APP_SIZE=""
WORKER_SIZE=""
REDIS_SIZE=""
POSTGRES_SIZE=""
POSTGRES_SKU=""
POSTGRES_STORAGE_MB=""
REDIS_TIER=""
WHAT_IF="false"

usage() { sed -n '2,19p' "$0" | tr -d '#'; exit 1; }

while getopts "g:f:a:w:e:o:k:m:c:x" opt; do
  case "$opt" in
    g) RESOURCE_GROUP="$OPTARG" ;;
    f) PARAMETERS_FILE="$OPTARG" ;;
    a) SEGUE_APP_SIZE="$OPTARG" ;;
    w) WORKER_SIZE="$OPTARG" ;;
    e) REDIS_SIZE="$OPTARG" ;;
    o) POSTGRES_SIZE="$OPTARG" ;;
    k) POSTGRES_SKU="$OPTARG" ;;
    m) POSTGRES_STORAGE_MB="$OPTARG" ;;
    c) REDIS_TIER="$OPTARG" ;;
    x) WHAT_IF="true" ;;
    *) usage ;;
  esac
done

[[ -n "$RESOURCE_GROUP" && -n "$PARAMETERS_FILE" ]] || usage
[[ -f "$PARAMETERS_FILE" ]] || { echo "Parameters file not found: $PARAMETERS_FILE" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE_FILE="${SCRIPT_DIR}/../azure-deploy/main.bicep"

assert_az() {
  command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) not found."; exit 1; }
  az account show >/dev/null 2>&1 || { echo "Not logged in. Run: az login"; exit 1; }
}

PARAM_ARGS=()
[[ -n "$SEGUE_APP_SIZE" ]] && PARAM_ARGS+=(--parameters "segueAppSize=${SEGUE_APP_SIZE}")
[[ -n "$WORKER_SIZE" ]] && PARAM_ARGS+=(--parameters "workerSize=${WORKER_SIZE}")
[[ -n "$REDIS_SIZE" ]] && PARAM_ARGS+=(--parameters "redisSize=${REDIS_SIZE}")
[[ -n "$POSTGRES_SIZE" ]] && PARAM_ARGS+=(--parameters "postgresSize=${POSTGRES_SIZE}")
[[ -n "$POSTGRES_SKU" ]] && PARAM_ARGS+=(--parameters "azurePostgresqlSku=${POSTGRES_SKU}")
[[ -n "$POSTGRES_STORAGE_MB" ]] && PARAM_ARGS+=(--parameters "azurePostgresqlStorageMb=${POSTGRES_STORAGE_MB}")
[[ -n "$REDIS_TIER" ]] && PARAM_ARGS+=(--parameters "azureCacheForRedisTier=${REDIS_TIER}")

if [[ ${#PARAM_ARGS[@]} -eq 0 ]]; then
  echo "No size/SKU changes given -- pass at least one of -a/-w/-e/-o/-k/-m/-c." >&2
  exit 1
fi

assert_az

if [[ -n "$POSTGRES_STORAGE_MB" ]]; then
  # Azure Database for PostgreSQL Flexible Server storage can only be scaled up, never down --
  # catch a decrease here with a clear message instead of letting the deployment fail partway.
  current_gb="$(az postgres flexible-server list --resource-group "$RESOURCE_GROUP" \
    --query "[?ends_with(name, '-pg')].storage.storageSizeGb | [0]" -o tsv 2>/dev/null || true)"
  if [[ -n "$current_gb" ]]; then
    requested_gb=$(( POSTGRES_STORAGE_MB / 1024 ))
    if (( requested_gb < current_gb )); then
      echo "Requested Postgres storage (${requested_gb}GB) is smaller than the current size (${current_gb}GB) -- Azure does not support shrinking Flexible Server storage." >&2
      exit 1
    fi
  fi
fi

if [[ "$WHAT_IF" == "true" ]]; then
  echo "==> Previewing resource change(s) in '${RESOURCE_GROUP}' (no changes applied)"
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$TEMPLATE_FILE" \
    --parameters "@${PARAMETERS_FILE}" \
    "${PARAM_ARGS[@]}"
  exit 0
fi

echo "==> Applying resource change(s) in '${RESOURCE_GROUP}'"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$TEMPLATE_FILE" \
  --parameters "@${PARAMETERS_FILE}" \
  "${PARAM_ARGS[@]}" \
  --query "properties.outputs" -o json

echo "Done."
