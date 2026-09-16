#!/usr/bin/env bash
# Same behaviour as update-application.ps1 -- see that file's header for the "minimum steps"
# reasoning (image tag bump + one incremental az deployment covers code, auto-migrated database,
# and any other resource changes already sitting in -f's parameters file).
#
# Usage:
#   ./update-application.sh -g rg-xxx -f ./main.parameters.json -t v1.3.0
#   ./update-application.sh -g rg-xxx -f ./main.parameters.json -t v1.3.0 -r myregistry.azurecr.io -b
#   ./update-application.sh ... -w   # preview only, changes nothing
set -euo pipefail

RESOURCE_GROUP=""
PARAMETERS_FILE=""
IMAGE_TAG=""
REGISTRY=""
BUILD_AND_PUSH="false"
WHAT_IF="false"

usage() { sed -n '2,9p' "$0" | tr -d '#'; exit 1; }

while getopts "g:f:t:r:bw" opt; do
  case "$opt" in
    g) RESOURCE_GROUP="$OPTARG" ;;
    f) PARAMETERS_FILE="$OPTARG" ;;
    t) IMAGE_TAG="$OPTARG" ;;
    r) REGISTRY="$OPTARG" ;;
    b) BUILD_AND_PUSH="true" ;;
    w) WHAT_IF="true" ;;
    *) usage ;;
  esac
done

[[ -n "$RESOURCE_GROUP" && -n "$PARAMETERS_FILE" && -n "$IMAGE_TAG" ]] || usage
[[ -f "$PARAMETERS_FILE" ]] || { echo "Parameters file not found: $PARAMETERS_FILE" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE_FILE="${SCRIPT_DIR}/../azure-deploy/main.bicep"

assert_az() {
  command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) not found."; exit 1; }
  az account show >/dev/null 2>&1 || { echo "Not logged in. Run: az login"; exit 1; }
}

assert_az

if [[ "$BUILD_AND_PUSH" == "true" ]]; then
  [[ -n "$REGISTRY" ]] || { echo "-b requires -r <registry>" >&2; exit 1; }
  echo "==> Building and pushing images tagged '${IMAGE_TAG}' to ${REGISTRY}"
  "${SCRIPT_DIR}/build-images.sh" -r "$REGISTRY" -t "$IMAGE_TAG" -p
fi

if [[ "$WHAT_IF" == "true" ]]; then
  echo "==> Previewing update to image tag '${IMAGE_TAG}' in resource group '${RESOURCE_GROUP}' (no changes applied)"
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$TEMPLATE_FILE" \
    --parameters "@${PARAMETERS_FILE}" \
    --parameters imageTag="$IMAGE_TAG"
  exit 0
fi

echo "==> Deploying image tag '${IMAGE_TAG}' to resource group '${RESOURCE_GROUP}'"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$TEMPLATE_FILE" \
  --parameters "@${PARAMETERS_FILE}" \
  --parameters imageTag="$IMAGE_TAG" \
  --query "properties.outputs" -o json

echo "Done. New revisions of segue-app/worker are rolling out on tag '${IMAGE_TAG}'; FHIRBridgeDb migrates automatically on boot."
