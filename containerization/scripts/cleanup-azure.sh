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
#   ./cleanup-azure.sh                 # name prefix read from terraform.tfvars, shows manifest/preview, terraform destroy, then an az-based sweep for anything Terraform's state didn't track
#   ./cleanup-azure.sh -n segue8       # explicit prefix override — drives the SAFETY GATE below AND the orphan sweep at the end (see their own comments)
#   ./cleanup-azure.sh -y              # skip both confirmation prompts (terraform destroy AND the sweep)
set -euo pipefail

ASSUME_YES="false"
NAME_PREFIX_ARG=""
while getopts "yn:" opt; do
  case "$opt" in
    y) ASSUME_YES="true" ;;
    n) NAME_PREFIX_ARG="$OPTARG" ;;
    *) echo "Usage: $0 [-y] [-n name_prefix]" >&2; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}/containerization/terraform/environments/azure"

if [[ ! -f terraform.tfstate && ! -d .terraform ]]; then
  echo "No local Terraform state found in $(pwd) — nothing to destroy from here." >&2
  echo "(If state is stored remotely, run 'terraform init' first so destroy can find it.)" >&2
  exit 1
fi

# Read the target resource group name + name_prefix straight from tfvars (both fall back to a
# default) — RG_NAME is purely for the tag-filtered fallback preview below (not used by
# `terraform destroy` itself, which is state-scoped); NAME_PREFIX additionally drives the safety
# gate further down.
RG_NAME="$(grep -E '^[[:space:]]*resource_group_name[[:space:]]*=' terraform.tfvars 2>/dev/null | sed -E 's/^[^=]*=[[:space:]]*"([^"]*)".*/\1/')"
RG_NAME="${RG_NAME:-rg-tusharpuri}"

# -n overrides; otherwise read from tfvars, falling back to "fhirbridge".
NAME_PREFIX="$(grep -E '^[[:space:]]*name_prefix[[:space:]]*=' terraform.tfvars 2>/dev/null | sed -E 's/^[^=]*=[[:space:]]*"([^"]*)".*/\1/')"
NAME_PREFIX="${NAME_PREFIX_ARG:-${NAME_PREFIX:-fhirbridge}}"
echo "Using name prefix '${NAME_PREFIX}' as the preview filter and pre-destroy safety gate (pass -n to override)."

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
    echo "Resources tagged Project=FHIRBridge AND named '${NAME_PREFIX}*' in resource group '${RG_NAME}' (before destroy):"
    az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='${RG_NAME}' && starts_with(name, '${NAME_PREFIX}')]" --output table 2>/dev/null \
      || echo "  (couldn't query — not logged in to az, or the group doesn't exist)"
    FOREIGN="$(az resource list --tag Project=FHIRBridge --query "[?resourceGroup=='${RG_NAME}' && !starts_with(name, '${NAME_PREFIX}')]" --output table 2>/dev/null || true)"
    if [[ -n "$FOREIGN" ]]; then
      echo
      echo "NOTE: other Project=FHIRBridge resources exist in '${RG_NAME}' but do NOT match prefix '${NAME_PREFIX}' — left untouched (e.g. the persistent vendor registry):"
      echo "$FOREIGN"
    fi
    echo
  fi
fi

# --- Safety gate: refuse to destroy if this state tracks anything outside NAME_PREFIX ---
#
# terraform destroy is state-scoped, not tag- or prefix-scoped — the preview above is purely
# informational. This is the check that actually prevents a repeat of the vendor-registry
# incident: it inspects every resource THIS state really tracks (via `terraform show -json`, the
# same source of truth destroy itself uses) and aborts before destroy runs at all if any of them
# has a real Azure name that doesn't start with $NAME_PREFIX — e.g. a resource that got imported
# here by mistake. Resources without a plain "name" attribute (access policies keyed by object_id,
# storage blobs, data sources, random_id, etc.) aren't a naming-collision risk and are skipped.
echo "==> Verifying every resource in this state matches name prefix '${NAME_PREFIX}' before destroying ..."
if command -v jq >/dev/null 2>&1; then
  MISMATCHES="$(terraform show -json 2>/dev/null | jq -r --arg prefix "$NAME_PREFIX" '
    .values.root_module.resources // [] | .[]
    | select(.mode == "managed" and (.values.name != null) and ((.values.name | startswith($prefix)) | not))
    | "\(.address) -> real Azure name '\''\(.values.name)'\'' (does not start with '\''\($prefix)'\'')"
  ' || true)"
  if [[ -n "$MISMATCHES" ]]; then
    echo >&2
    echo "ABORTING — this Terraform state tracks a resource that does NOT match name prefix '${NAME_PREFIX}':" >&2
    echo "$MISMATCHES" | sed 's/^/  - /' >&2
    echo >&2
    echo "This usually means a resource ended up in this state by mistake (e.g. the persistent vendor registry got imported/created here). Investigate with 'terraform state list' / 'terraform state show <address>', remove it from THIS state with 'terraform state rm <address>' if it doesn't belong here (that only detaches it from tracking — it does NOT delete the real resource), then re-run this script." >&2
    exit 1
  fi
  echo "OK — every resource in state matches prefix '${NAME_PREFIX}'."
else
  echo "  jq not found — skipping the safety gate; review the preview above carefully before confirming (or install jq for the automated check, or use cleanup-azure.ps1)." >&2
fi

if [[ "$ASSUME_YES" != "true" ]]; then
  read -r -p "Destroy everything shown above? [y/N] " reply
  [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
fi

# destroy still needs a value for every required variable to evaluate the config graph (same as
# apply), but never actually USES them — nothing is created or updated on the way to deleting
# everything. Rather than let Terraform interactively prompt for real secrets just to tear things
# down, backfill a throwaway placeholder for any required variable terraform.tfvars doesn't
# already define — values that ARE in tfvars are left alone and used as-is.
REQUIRED_VARS=(postgres_password jwt_signing_key redis_password)
DESTROY_VAR_ARGS=()
for v in "${REQUIRED_VARS[@]}"; do
  if ! grep -qE "^[[:space:]]*${v}[[:space:]]*=" terraform.tfvars 2>/dev/null; then
    DESTROY_VAR_ARGS+=(-var "${v}=destroy-only-placeholder")
  fi
done
if [[ ${#DESTROY_VAR_ARGS[@]} -gt 0 ]]; then
  echo "terraform.tfvars doesn't define every required variable — supplying throwaway placeholder values for this destroy only (safe: destroy never uses them to create or update anything)."
fi

set +e
# -auto-approve regardless: the confirmation gate above already covers it (unless -y was passed
# to skip that too), so Terraform's own separate "type yes" prompt would just be a redundant
# second confirmation of the same action.
terraform destroy -auto-approve "${DESTROY_VAR_ARGS[@]}"
DESTROY_EXIT=$?
set -e

if [[ $DESTROY_EXIT -eq 0 ]]; then
  echo "Done. Everything this Terraform config's OWN STATE tracked has been removed; the resource group itself was left in place (it wasn't created by this config)."
else
  echo "terraform destroy exited with errors (code ${DESTROY_EXIT}) — some resources may not have been fully removed. Check the error above; a common one is a Key Vault purge permission error, which is harmless (the vault is still soft-deleted, just not immediately purged)." >&2
fi

# --- Orphan sweep: catch resources this state doesn't track but still match NAME_PREFIX ---
#
# `terraform destroy` above is entirely state-scoped — it reports "0 to destroy" for a deployment
# that was applied from a different machine/directory (its state never made it here), even though
# the real Azure resources are still sitting in the resource group. This sweep catches that case:
# it looks for anything named "${NAME_PREFIX}*" directly in the resource group (NOT a
# Project=FHIRBridge tag match — this resource group is known to hold unrelated infrastructure
# alongside these deployments, e.g. a dev VM, healthcare API workspaces, other storage accounts,
# even an unrelated second Container Apps deployment — a tag alone isn't tight enough scoping
# here), shows exactly what it found, and asks before deleting. Deletes in dependency-safe batches
# (managed certificates, then Container Apps, then the Container Apps Environment they ran in,
# then whatever is left) rather than one bulk call, since ARM doesn't guarantee ordering across an
# arbitrary --ids list. Needs jq to group by type safely — without it, this step is skipped with a
# pointer to do it manually (same as the safety gate above).
if command -v az >/dev/null 2>&1; then
  echo
  echo "==> Sweeping for any remaining resources named '${NAME_PREFIX}*' in '${RG_NAME}' (catches deployments this state doesn't track) ..."
  if command -v jq >/dev/null 2>&1; then
    REMAINING_JSON="$(az resource list --resource-group "${RG_NAME}" --query "[?starts_with(name, '${NAME_PREFIX}')]" -o json 2>/dev/null || echo '[]')"
    REMAINING_COUNT="$(echo "$REMAINING_JSON" | jq 'length')"

    if [[ "$REMAINING_COUNT" -gt 0 ]]; then
      echo "Found ${REMAINING_COUNT} resource(s) matching '${NAME_PREFIX}*' still in '${RG_NAME}':"
      echo "$REMAINING_JSON" | jq -r '.[] | "  - \(.name) (\(.type))"'

      DO_SWEEP="$ASSUME_YES"
      if [[ "$ASSUME_YES" != "true" ]]; then
        read -r -p "Delete these via 'az resource delete' (separate from the Terraform destroy above)? [y/N] " reply2
        [[ "$reply2" =~ ^[Yy]$ ]] && DO_SWEEP="true" || DO_SWEEP="false"
      fi

      if [[ "$DO_SWEEP" == "true" ]]; then
        # Managed certificates FIRST, then apps, then the environment. Confirmed empirically
        # (2026-08-28, segue10 cleanup): deleting an app that still has a bound managed
        # certificate is SLOW (~20 min for one app, via `az containerapp delete`) and can even get
        # stuck in a broken half-deleted state (ingress stripped but provisioningState stuck at
        # "Failed" forever - recovered only by a generic `az resource delete --ids` against the
        # same resource, which is exactly what this sweep already uses). Deleting the certificate
        # FIRST removes that entanglement up front - a test app whose pending cert was deleted
        # first took 3 seconds to delete, vs. 20+ minutes for one whose succeeded cert was still
        # attached. If a cert delete fails here (e.g. a live SniEnabled binding that genuinely
        # can't be dropped that easily), it's non-fatal - the subsequent app delete just falls
        # back to the slower path, same as before this fix. The environment goes last since it's
        # the parent of both.
        for t in "Microsoft.App/managedEnvironments/managedCertificates" "Microsoft.App/containerApps" "Microsoft.App/managedEnvironments"; do
          BATCH_IDS="$(echo "$REMAINING_JSON" | jq -r --arg t "$t" '.[] | select(.type == $t) | .id')"
          if [[ -n "$BATCH_IDS" ]]; then
            echo "  Deleting resource(s) of type ${t} ..."
            # shellcheck disable=SC2086
            az resource delete --ids $BATCH_IDS
          fi
        done
        REST_IDS="$(echo "$REMAINING_JSON" | jq -r '.[] | select(.type != "Microsoft.App/managedEnvironments/managedCertificates" and .type != "Microsoft.App/containerApps" and .type != "Microsoft.App/managedEnvironments") | .id')"
        if [[ -n "$REST_IDS" ]]; then
          echo "  Deleting remaining resource(s) ..."
          # shellcheck disable=SC2086
          az resource delete --ids $REST_IDS
        fi
        echo "Sweep complete. If any resource above still failed to delete, re-run this script - a common cause is a Container Apps Environment deletion (slow, several minutes) that hadn't finished cascading yet."
      else
        echo "Skipped — left as-is."
      fi
    else
      echo "None found — nothing to sweep."
    fi
  else
    echo "  jq not found — skipping the automated sweep. Review manually: az resource list --resource-group ${RG_NAME} --query \"[?starts_with(name, '${NAME_PREFIX}')]\" -o table (or use cleanup-azure.ps1)." >&2
  fi

  echo
  echo "Resources named '${NAME_PREFIX}*' remaining in '${RG_NAME}' (should be empty):"
  az resource list --resource-group "${RG_NAME}" --query "[?starts_with(name, '${NAME_PREFIX}')]" --output table 2>/dev/null \
    || echo "  (couldn't query — not logged in to az)"
fi

exit $DESTROY_EXIT
