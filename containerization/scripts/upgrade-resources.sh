#!/usr/bin/env bash
# Same behaviour as upgrade-resources.ps1 -- see that file's header for the manifest-mode /
# legacy-file-mode explanation (main.bicep's own `deploymentManifest` output supplies every
# non-secret setting automatically; only Postgres password, JWT signing key, and Redis
# password/thumbprint -- if Redis is containerized -- must be passed explicitly, since Azure never
# returns secret parameter values). Only pass the size/SKU flags for what actually needs to change;
# everything else keeps its current value (from the manifest, or from -f in legacy mode).
#
# Usage:
#   ./upgrade-resources.sh -g rg-xxx -a Large -p '...' -j '...'
#   ./upgrade-resources.sh -g rg-xxx -w Medium -k GeneralPurpose_D2s_v3 -m 65536 -p '...' -j '...'
#   ./upgrade-resources.sh -g rg-xxx -f ./main.parameters.json -a Large   # legacy file mode
#   ./upgrade-resources.sh ... -x   # preview only, changes nothing
#
# -a SEGUE_APP_SIZE   Small|Medium|Large|XLarge
# -w WORKER_SIZE      Small|Medium|Large|XLarge
# -e REDIS_SIZE       Small|Medium|Large|XLarge   (containerized Redis path only)
# -o POSTGRES_SIZE    Small|Medium|Large|XLarge   (containerized Postgres path only)
# -k POSTGRES_SKU     Burstable_B1ms|Burstable_B2s|GeneralPurpose_D2s_v3|GeneralPurpose_D4s_v3
# -m POSTGRES_STORAGE_MB   32768|65536|131072|262144 (up only -- Azure can't shrink Flexible Server storage)
# -c REDIS_TIER       Balanced_B0|Balanced_B5|MemoryOptimized_M10   (managed Redis path only)
# -p POSTGRES_PASSWORD / -j JWT_SIGNING_KEY   required in manifest mode
# -r REDIS_PASSWORD / -t REDIS_THUMBPRINT     required in manifest mode when Redis is containerized
# -f PARAMETERS_FILE  legacy file mode -- reuses a saved parameters JSON wholesale instead
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
POSTGRES_PASSWORD=""
JWT_SIGNING_KEY=""
REDIS_PASSWORD=""
REDIS_THUMBPRINT=""
IMAGE_REGISTRY_USERNAME=""
IMAGE_REGISTRY_PASSWORD=""
WHAT_IF="false"

usage() { sed -n '2,22p' "$0" | tr -d '#'; exit 1; }

while getopts "g:f:a:w:e:o:k:m:c:p:j:r:t:u:i:x" opt; do
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
    p) POSTGRES_PASSWORD="$OPTARG" ;;
    j) JWT_SIGNING_KEY="$OPTARG" ;;
    r) REDIS_PASSWORD="$OPTARG" ;;
    t) REDIS_THUMBPRINT="$OPTARG" ;;
    u) IMAGE_REGISTRY_USERNAME="$OPTARG" ;;
    i) IMAGE_REGISTRY_PASSWORD="$OPTARG" ;;
    x) WHAT_IF="true" ;;
    *) usage ;;
  esac
done

[[ -n "$RESOURCE_GROUP" ]] || usage

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE_FILE="${SCRIPT_DIR}/../azure-deploy/main.bicep"

assert_az() {
  command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) not found."; exit 1; }
  az account show >/dev/null 2>&1 || { echo "Not logged in. Run: az login"; exit 1; }
}

OVERRIDE_ARGS=()
[[ -n "$SEGUE_APP_SIZE" ]] && OVERRIDE_ARGS+=(--parameters "segueAppSize=${SEGUE_APP_SIZE}")
[[ -n "$WORKER_SIZE" ]] && OVERRIDE_ARGS+=(--parameters "workerSize=${WORKER_SIZE}")
[[ -n "$REDIS_SIZE" ]] && OVERRIDE_ARGS+=(--parameters "redisSize=${REDIS_SIZE}")
[[ -n "$POSTGRES_SIZE" ]] && OVERRIDE_ARGS+=(--parameters "postgresSize=${POSTGRES_SIZE}")
[[ -n "$POSTGRES_SKU" ]] && OVERRIDE_ARGS+=(--parameters "azurePostgresqlSku=${POSTGRES_SKU}")
[[ -n "$POSTGRES_STORAGE_MB" ]] && OVERRIDE_ARGS+=(--parameters "azurePostgresqlStorageMb=${POSTGRES_STORAGE_MB}")
[[ -n "$REDIS_TIER" ]] && OVERRIDE_ARGS+=(--parameters "azureCacheForRedisTier=${REDIS_TIER}")

if [[ ${#OVERRIDE_ARGS[@]} -eq 0 ]]; then
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

PARAM_ARGS=()

if [[ -n "$PARAMETERS_FILE" ]]; then
  [[ -f "$PARAMETERS_FILE" ]] || { echo "Parameters file not found: $PARAMETERS_FILE" >&2; exit 1; }
  echo "==> Legacy file mode: reusing ${PARAMETERS_FILE}"
  PARAM_ARGS+=(--parameters "@${PARAMETERS_FILE}")
else
  echo "==> Manifest mode: reading prior deployment settings from resource group '${RESOURCE_GROUP}'"
  manifest_query() {
    az deployment group show --resource-group "$RESOURCE_GROUP" --name main \
      --query "properties.outputs.deploymentManifest.value.$1" -o tsv 2>/dev/null
  }
  USE_AZURE_POSTGRESQL="$(manifest_query useAzurePostgresql || true)"
  if [[ -z "$USE_AZURE_POSTGRESQL" ]]; then
    echo "No prior deployment manifest found (az deployment group show --name main). Either this resource group has no main.bicep deployment yet, its deployment history was purged, or it predates this manifest. Re-run with -f pointing at your original parameters file, or use the full Deploy-to-Azure wizard instead." >&2
    exit 1
  fi

  [[ -n "$POSTGRES_PASSWORD" && -n "$JWT_SIGNING_KEY" ]] || {
    echo "Manifest mode requires -p (Postgres password) and -j (JWT signing key) -- the same values your original install used. Azure never returns secret parameter values, so these can't be read back automatically." >&2
    exit 1
  }

  USE_AZURE_CACHE_FOR_REDIS="$(manifest_query useAzureCacheForRedis)"
  if [[ "$USE_AZURE_CACHE_FOR_REDIS" != "true" ]]; then
    [[ -n "$REDIS_PASSWORD" && -n "$REDIS_THUMBPRINT" ]] || {
      echo "This install's Redis is Containerized (per the deployment manifest) -- -r (Redis password) and -t (Redis TLS thumbprint) are required (the same values your original install used)." >&2
      exit 1
    }
  fi

  PARAM_ARGS+=(--parameters "namePrefix=$(manifest_query namePrefix)")
  PARAM_ARGS+=(--parameters "location=$(manifest_query location)")
  PARAM_ARGS+=(--parameters "imageTag=$(manifest_query imageTag)")
  PARAM_ARGS+=(--parameters "useAzurePostgresql=${USE_AZURE_POSTGRESQL}")
  PARAM_ARGS+=(--parameters "azurePostgresqlSku=$(manifest_query azurePostgresqlSku)")
  PARAM_ARGS+=(--parameters "azurePostgresqlStorageMb=$(manifest_query azurePostgresqlStorageMb)")
  PARAM_ARGS+=(--parameters "postgresSize=$(manifest_query postgresSize)")
  PARAM_ARGS+=(--parameters "useAzureCacheForRedis=${USE_AZURE_CACHE_FOR_REDIS}")
  PARAM_ARGS+=(--parameters "azureCacheForRedisTier=$(manifest_query azureCacheForRedisTier)")
  PARAM_ARGS+=(--parameters "redisSize=$(manifest_query redisSize)")
  PARAM_ARGS+=(--parameters "segueAppSize=$(manifest_query segueAppSize)")
  PARAM_ARGS+=(--parameters "workerSize=$(manifest_query workerSize)")
  PARAM_ARGS+=(--parameters "enableTenantSecretsKeyVault=$(manifest_query enableTenantSecretsKeyVault)")
  PARAM_ARGS+=(--parameters "storageRedundancy=$(manifest_query storageRedundancy)")
  PARAM_ARGS+=(--parameters "enableSeq=$(manifest_query enableSeq)")
  PARAM_ARGS+=(--parameters "seqSize=$(manifest_query seqSize)")
  PARAM_ARGS+=(--parameters "enableFrontDoorWaf=$(manifest_query enableFrontDoorWaf)")
  PARAM_ARGS+=(--parameters "wafPolicyMode=$(manifest_query wafPolicyMode)")
  PARAM_ARGS+=(--parameters "imageRegistryServer=$(manifest_query imageRegistryServer)")
  PARAM_ARGS+=(--parameters "postgresPassword=${POSTGRES_PASSWORD}")
  PARAM_ARGS+=(--parameters "jwtSigningKey=${JWT_SIGNING_KEY}")
  if [[ "$USE_AZURE_CACHE_FOR_REDIS" != "true" ]]; then
    PARAM_ARGS+=(--parameters "redisPassword=${REDIS_PASSWORD}")
    PARAM_ARGS+=(--parameters "redisTrustedCertificateThumbprint=${REDIS_THUMBPRINT}")
  fi
  [[ -n "$IMAGE_REGISTRY_USERNAME" ]] && PARAM_ARGS+=(--parameters "imageRegistryUsername=${IMAGE_REGISTRY_USERNAME}")
  [[ -n "$IMAGE_REGISTRY_PASSWORD" ]] && PARAM_ARGS+=(--parameters "imageRegistryPassword=${IMAGE_REGISTRY_PASSWORD}")
fi

# Overrides listed AFTER the manifest/file baseline -- az CLI applies later --parameters values on
# top of earlier ones when the same key repeats, so these win over whatever the baseline set.
PARAM_ARGS+=("${OVERRIDE_ARGS[@]}")

if [[ "$WHAT_IF" == "true" ]]; then
  echo "==> Previewing resource change(s) in '${RESOURCE_GROUP}' (no changes applied)"
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$TEMPLATE_FILE" \
    "${PARAM_ARGS[@]}"
  exit 0
fi

echo "==> Applying resource change(s) in '${RESOURCE_GROUP}'"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$TEMPLATE_FILE" \
  "${PARAM_ARGS[@]}" \
  --query "properties.outputs" -o json

echo "Done."
