#!/usr/bin/env bash
# Tears down everything the Azure Terraform environment created inside its target resource group —
# ACR, Log Analytics workspace, Container Apps Environment, Key Vault, storage account/shares, and
# all 5 Container Apps. The resource group itself is a pre-existing one this config only deploys
# INTO (see the data "azurerm_resource_group" "main" block in main.tf) — `terraform destroy` removes
# everything Terraform created but leaves the resource group itself intact, since it wasn't created
# by this config and may be shared with other things.
#
# Usage:
#   ./cleanup-azure.sh          # prompts for confirmation, then terraform destroy
#   ./cleanup-azure.sh -y       # skip the confirmation prompt (-auto-approve)
set -euo pipefail

ASSUME_YES="false"
while getopts "y" opt; do
  case "$opt" in
    y) ASSUME_YES="true" ;;
    *) echo "Usage: $0 [-y]" >&2; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}/containerization/terraform/environments/azure"

if [[ ! -f terraform.tfstate && ! -d .terraform ]]; then
  echo "No local Terraform state found in $(pwd) — nothing to destroy from here." >&2
  echo "(If state is stored remotely, run 'terraform init' first so destroy can find it.)" >&2
  exit 1
fi

# Read the target resource group name straight from tfvars (falls back to the variable default) —
# purely for the tag-filtered preview below, not used by `terraform destroy` itself (that's
# state-scoped and never touches anything outside what Terraform created, tagged or not).
RG_NAME="$(grep -E '^[[:space:]]*resource_group_name[[:space:]]*=' terraform.tfvars 2>/dev/null | sed -E 's/^[^=]*=[[:space:]]*"([^"]*)".*/\1/')"
RG_NAME="${RG_NAME:-rg-tusharpuri}"

# az CLI rejects combining --tag with --resource-group on `az resource list` ("you cannot use
# '--tag' with '--resource-group'") — so filter by tag across the subscription instead and narrow
# to this resource group client-side via --query.
if command -v az >/dev/null 2>&1; then
  echo "Resources tagged Project=FHIRBridge currently in resource group '${RG_NAME}' (before destroy):"
  az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='${RG_NAME}']" --output table 2>/dev/null \
    || echo "  (couldn't query — not logged in to az, or the group doesn't exist)"
  echo
fi

set +e
if [[ "$ASSUME_YES" == "true" ]]; then
  terraform destroy -auto-approve
else
  terraform destroy
fi
DESTROY_EXIT=$?
set -e

if [[ $DESTROY_EXIT -eq 0 ]]; then
  echo "Done. Everything this Terraform config created has been removed; the resource group itself was left in place (it wasn't created by this config)."
else
  echo "terraform destroy exited with errors (code ${DESTROY_EXIT}) — some resources may not have been fully removed. Check the error above; a common one is a Key Vault purge permission error, which is harmless (the vault is still soft-deleted, just not immediately purged)." >&2
fi

if command -v az >/dev/null 2>&1; then
  echo
  echo "Resources tagged Project=FHIRBridge remaining in '${RG_NAME}' (should be empty):"
  az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='${RG_NAME}']" --output table 2>/dev/null \
    || echo "  (couldn't query — not logged in to az)"
fi

exit $DESTROY_EXIT
