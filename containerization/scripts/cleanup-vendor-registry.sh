#!/usr/bin/env bash
# Tears down the persistent vendor Container Registry (containerization/terraform/vendor-registry)
# — a single ACR that holds published, compiled release images, kept separate from any per-client
# test deployment. The resource group itself is a pre-existing one this config only deploys INTO
# (see the data "azurerm_resource_group" "main" block in main.tf) — `terraform destroy` removes
# the registry but leaves the resource group itself intact, since it wasn't created by this config
# and (today) is shared with the ../environments/azure test deployment too.
#
# Usage:
#   ./cleanup-vendor-registry.sh          # prompts for confirmation, then terraform destroy
#   ./cleanup-vendor-registry.sh -y       # skip the confirmation prompt (-auto-approve)
set -euo pipefail

ASSUME_YES="false"
while getopts "y" opt; do
  case "$opt" in
    y) ASSUME_YES="true" ;;
    *) echo "Usage: $0 [-y]" >&2; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}/containerization/terraform/vendor-registry"

if [[ ! -f terraform.tfstate && ! -d .terraform ]]; then
  echo "No local Terraform state found in $(pwd) — nothing to destroy from here." >&2
  echo "(If state is stored remotely, run 'terraform init' first so destroy can find it.)" >&2
  exit 1
fi

# Read the target resource group from tfvars (falls back to the variable default) — purely for
# the tag-filtered preview below, not used by `terraform destroy` itself (that's state-scoped and
# never touches anything outside what Terraform created, tagged or not).
RG_NAME="$(grep -E '^[[:space:]]*resource_group_name[[:space:]]*=' terraform.tfvars 2>/dev/null | sed -E 's/^[^=]*=[[:space:]]*"([^"]*)".*/\1/')"
RG_NAME="${RG_NAME:-rg-tusharpuri}"

# Filters on BOTH Project and Component tags (not just Project), since this resource group may
# currently also hold an unrelated ../environments/azure test deployment tagged
# Component=containerization — without the Component filter, this preview would show both and make
# it unclear whether cleanup actually worked.
if command -v az >/dev/null 2>&1; then
  echo "Vendor-registry resources currently in '${RG_NAME}' (before destroy):"
  az resource list --tag Project=Segue --query "[?resourceGroup=='${RG_NAME}' && tags.Component=='vendor-registry']" --output table 2>/dev/null \
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
  echo "Done. The vendor registry has been removed; the resource group itself was left in place (it wasn't created by this config)."
else
  echo "terraform destroy exited with errors (code ${DESTROY_EXIT}) — the registry may not have been fully removed. Check the error above." >&2
fi

if command -v az >/dev/null 2>&1; then
  echo
  echo "Vendor-registry resources remaining in '${RG_NAME}' (should be empty):"
  az resource list --tag Project=Segue --query "[?resourceGroup=='${RG_NAME}' && tags.Component=='vendor-registry']" --output table 2>/dev/null \
    || echo "  (couldn't query — not logged in to az)"
fi

exit $DESTROY_EXIT
