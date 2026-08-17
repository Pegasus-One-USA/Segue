#!/usr/bin/env bash
# Tears down everything the Azure Terraform environment created inside its target resource group —
# ACR, Log Analytics workspace, Container Apps Environment, Key Vault, storage account/shares, and
# all 5 Container Apps. The resource group itself is a pre-existing one this config only deploys
# INTO (see the data "azurerm_resource_group" "main" block in main.tf) — `terraform destroy` removes
# everything Terraform created but leaves the resource group itself intact, since it wasn't created
# by this config and may be shared with other things.
#
# Before destroying, this downloads and shows main.tf's own resource-manifest blob (resources.txt,
# in the storage account this config creates) as the preview — not a Terraform state query, an
# actual file that survives independently of state. It's downloaded BEFORE `terraform destroy`
# runs, while the blob (and everything else) still exists. Since that manifest is computed directly
# from Terraform's own resource references, it's already precise — no selection step needed here,
# just a clear preview and a plain confirm. Falls back to the tag-filtered `az resource list`
# preview only if the manifest can't be fetched (e.g. a very old deployment made before this output
# existed).
#
# The actual deletion still goes through `terraform destroy`, not manual `az resource delete` calls
# — that's what correctly handles dependency order AND keeps Terraform's state in sync afterward.
#
# Usage:
#   ./cleanup-azure.sh          # shows the manifest (or tag-based fallback), then terraform destroy
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
# purely for the tag-filtered fallback preview below, not used by `terraform destroy` itself
# (that's state-scoped and never touches anything outside what Terraform created, tagged or not).
RG_NAME="$(grep -E '^[[:space:]]*resource_group_name[[:space:]]*=' terraform.tfvars 2>/dev/null | sed -E 's/^[^=]*=[[:space:]]*"([^"]*)".*/\1/')"
RG_NAME="${RG_NAME:-rg-tusharpuri}"

MANIFEST_SHOWN="false"
if command -v az >/dev/null 2>&1; then
  echo "==> Fetching the resource manifest (resources.txt) before anything is destroyed ..."
  MANIFEST_LOCAL_FILE="$(mktemp -t fhirbridge-resources-preview.XXXXXX)"
  DOWNLOAD_CMD="$(terraform output -raw resource_manifest_download_cmd 2>/dev/null || true)"
  if [[ -n "$DOWNLOAD_CMD" ]]; then
    CMD_WITH_LOCAL_PATH="$(echo "$DOWNLOAD_CMD" | sed -E "s#--file [^ ]+#--file \"${MANIFEST_LOCAL_FILE}\"#")"
    if eval "$CMD_WITH_LOCAL_PATH --only-show-errors" >/dev/null 2>&1 && [[ -s "$MANIFEST_LOCAL_FILE" ]]; then
      echo ""
      echo "----- resources.txt (fetched just now, before destroy) -----"
      cat "$MANIFEST_LOCAL_FILE"
      echo "--------------------------------------------------------------"
      echo ""
      MANIFEST_SHOWN="true"
    fi
  fi
  rm -f "$MANIFEST_LOCAL_FILE"

  if [[ "$MANIFEST_SHOWN" != "true" ]]; then
    echo "Couldn't fetch resources.txt (older deployment, or not logged in to az) — falling back to a tag-based preview instead."
    # az CLI rejects combining --tag with --resource-group on `az resource list` ("you cannot use
    # '--tag' with '--resource-group'") — so filter by tag across the subscription instead and
    # narrow to this resource group client-side via --query.
    echo "Resources tagged Project=Segue currently in resource group '${RG_NAME}' (before destroy):"
    az resource list --tag Project=Segue --query "[?resourceGroup=='${RG_NAME}']" --output table 2>/dev/null \
      || echo "  (couldn't query — not logged in to az, or the group doesn't exist)"
    echo
  fi
fi

if [[ "$ASSUME_YES" != "true" ]]; then
  read -r -p "Destroy everything shown above? [y/N] " reply
  [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
fi

set +e
# -auto-approve regardless: the confirmation gate above already covers it (unless -y was passed
# to skip that too), so Terraform's own separate "type yes" prompt would just be a redundant
# second confirmation of the same action.
terraform destroy -auto-approve
DESTROY_EXIT=$?
set -e

if [[ $DESTROY_EXIT -eq 0 ]]; then
  echo "Done. Everything this Terraform config created has been removed; the resource group itself was left in place (it wasn't created by this config)."
else
  echo "terraform destroy exited with errors (code ${DESTROY_EXIT}) — some resources may not have been fully removed. Check the error above; a common one is a Key Vault purge permission error, which is harmless (the vault is still soft-deleted, just not immediately purged)." >&2
fi

if command -v az >/dev/null 2>&1; then
  echo
  echo "Resources tagged Project=Segue remaining in '${RG_NAME}' (should be empty):"
  az resource list --tag Project=Segue --query "[?resourceGroup=='${RG_NAME}']" --output table 2>/dev/null \
    || echo "  (couldn't query — not logged in to az)"
fi

exit $DESTROY_EXIT
