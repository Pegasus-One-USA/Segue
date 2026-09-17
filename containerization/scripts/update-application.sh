#!/usr/bin/env bash
# Same behaviour as update-application.ps1 -- see that file's header for the manifest-mode /
# legacy-file-mode explanation (main.bicep's own `deploymentManifest` output supplies every
# non-secret setting automatically; only Postgres password, JWT signing key, and Redis
# password/thumbprint -- if Redis is containerized -- must be passed explicitly, since Azure never
# returns secret parameter values).
#
# Usage:
#   ./update-application.sh -g rg-xxx -t v1.3.0 -p '...' -j '...'
#   ./update-application.sh -g rg-xxx -t v1.3.0 -p '...' -j '...' -e '...' -h '...'
#   ./update-application.sh -g rg-xxx -f ./main.parameters.json -t v1.3.0   # legacy file mode
#   ./update-application.sh ... -r myregistry.azurecr.io -b
#   ./update-application.sh ... -w   # preview only, changes nothing
set -euo pipefail

RESOURCE_GROUP=""
IMAGE_TAG=""
POSTGRES_PASSWORD=""
JWT_SIGNING_KEY=""
REDIS_PASSWORD=""
REDIS_THUMBPRINT=""
IMAGE_REGISTRY_USERNAME=""
IMAGE_REGISTRY_PASSWORD=""
PARAMETERS_FILE=""
REGISTRY=""
BUILD_AND_PUSH="false"
WHAT_IF="false"

usage() { sed -n '2,12p' "$0" | tr -d '#'; exit 1; }

while getopts "g:t:p:j:e:h:f:r:bw" opt; do
  case "$opt" in
    g) RESOURCE_GROUP="$OPTARG" ;;
    t) IMAGE_TAG="$OPTARG" ;;
    p) POSTGRES_PASSWORD="$OPTARG" ;;
    j) JWT_SIGNING_KEY="$OPTARG" ;;
    e) REDIS_PASSWORD="$OPTARG" ;;
    h) REDIS_THUMBPRINT="$OPTARG" ;;
    f) PARAMETERS_FILE="$OPTARG" ;;
    r) REGISTRY="$OPTARG" ;;
    b) BUILD_AND_PUSH="true" ;;
    w) WHAT_IF="true" ;;
    *) usage ;;
  esac
done

[[ -n "$RESOURCE_GROUP" && -n "$IMAGE_TAG" ]] || usage

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE_FILE="${SCRIPT_DIR}/../azure-deploy/main.bicep"

assert_az() {
  command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) not found."; exit 1; }
  az account show >/dev/null 2>&1 || { echo "Not logged in. Run: az login"; exit 1; }
}

# az's own boolean output is already the lowercase "true"/"false" ARM expects, so manifest values
# read via --query -o tsv need no conversion here (unlike PowerShell's ConvertFrom-Json booleans).

assert_az

if [[ "$BUILD_AND_PUSH" == "true" ]]; then
  [[ -n "$REGISTRY" ]] || { echo "-b requires -r <registry>" >&2; exit 1; }
  echo "==> Building and pushing images tagged '${IMAGE_TAG}' to ${REGISTRY}"
  "${SCRIPT_DIR}/build-images.sh" -r "$REGISTRY" -t "$IMAGE_TAG" -p
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
      echo "This install's Redis is Containerized (per the deployment manifest) -- -e (Redis password) and -h (Redis TLS thumbprint) are required (the same values your original install used)." >&2
      exit 1
    }
  fi

  PARAM_ARGS+=(--parameters "namePrefix=$(manifest_query namePrefix)")
  PARAM_ARGS+=(--parameters "location=$(manifest_query location)")
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

PARAM_ARGS+=(--parameters "imageTag=${IMAGE_TAG}")

if [[ "$WHAT_IF" == "true" ]]; then
  echo "==> Previewing update to image tag '${IMAGE_TAG}' in resource group '${RESOURCE_GROUP}' (no changes applied)"
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$TEMPLATE_FILE" \
    "${PARAM_ARGS[@]}"
  exit 0
fi

echo "==> Deploying image tag '${IMAGE_TAG}' to resource group '${RESOURCE_GROUP}'"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$TEMPLATE_FILE" \
  "${PARAM_ARGS[@]}" \
  --query "properties.outputs" -o json

echo "Done. New revisions of segue-app/worker are rolling out on tag '${IMAGE_TAG}'; FHIRBridgeDb migrates automatically on boot."
