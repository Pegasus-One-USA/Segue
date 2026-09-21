#!/usr/bin/env bash
# Tears down a Bicep (main.bicep) deployment. Unlike the Terraform environments, this path has no
# state file to destroy from — cleanup prefers reading main.bicep's own `resourceManifest` output
# (every resource ID it created, in a dependency-safe deletion order) straight out of the
# deployment's own history — no extra resource needed to store it, Azure keeps deployment outputs
# automatically. This is precise even in a shared resource group, since it only ever offers those
# exact IDs for deletion. Falls back to the resource group being dedicated to this deployment
# (recommended, see README.md's Tier 1 flow), or to the commonTags every taggable resource in
# main.bicep carries (Project/Component/Environment/ManagedBy), only if that manifest isn't
# available — e.g. an older deployment made before this output existed, or its history was purged.
#
# The tag-based mode filters on BOTH Environment=<value> AND ManagedBy=Bicep, not Environment
# alone: a shared resource group can easily also hold a Terraform-managed deployment (../terraform
# environments, ../terraform/vendor-registry) using the SAME Environment tag value (both default to
# "segue") — those are tagged ManagedBy=Terraform, not Bicep, so this second condition is what
# actually keeps this script from also deleting them.
#
# Interactive selection only applies to the TAG-BASED fallback below, where there's genuine
# ambiguity about what should be included — by default (no -y) that list is shown numbered and you
# type which ones to actually delete (comma-separated indices, "all", or blank to abort). The
# MANIFEST path doesn't use a picker at all: it's already a precise, trusted list computed directly
# from main.bicep's own resources, so it's just a plain preview + yes/no confirm. -y skips any
# prompt/picker entirely and deletes everything found, for unattended use.
#
# Usage:
#   ./cleanup.sh -g segue-rg -w        # preview only (-WhatIf) - prints the checklist, deletes nothing, ever
#   ./cleanup.sh -g segue-rg           # opens the numbered picker on whatever's found
#   ./cleanup.sh -e segue              # tag-based: picker over Environment=<value> AND ManagedBy=Bicep resources
#   ./cleanup.sh -g segue-rg -d main -y   # unattended, deletes everything found
set -euo pipefail

RESOURCE_GROUP=""
DEPLOYMENT_NAME="main"
ENVIRONMENT_TAG=""
ASSUME_YES="false"
WHAT_IF="false"

while getopts "g:d:e:yw" opt; do
  case "$opt" in
    g) RESOURCE_GROUP="$OPTARG" ;;
    d) DEPLOYMENT_NAME="$OPTARG" ;;
    e) ENVIRONMENT_TAG="$OPTARG" ;;
    y) ASSUME_YES="true" ;;
    w) WHAT_IF="true" ;;
    *) echo "Usage: $0 [-g resource-group] [-d deployment-name] [-e environment-tag-value] [-w] [-y]" >&2; exit 1 ;;
  esac
done

if [[ -z "$RESOURCE_GROUP" && -z "$ENVIRONMENT_TAG" ]]; then
  echo "Error: pass either -g <resource-group> (recommended) or -e <environment-tag-value>." >&2
  exit 1
fi

if ! command -v az >/dev/null 2>&1; then
  echo "Error: az CLI not found. Install it first (see ../../Documents/Containerization-Multi-Cloud-Guide.html)." >&2
  exit 1
fi

# Prints the given IDs numbered, prompts for which to delete, and echoes the selected ones
# (newline-separated) to stdout. Called only when not in unattended (-y) mode. An empty/blank
# reply means abort - caller must treat empty output as "nothing selected", not "delete nothing
# but continue".
pick_resources() {
  local ids=("$@")
  for i in "${!ids[@]}"; do
    echo "  [$i] ${ids[$i]}" >&2
  done
  read -r -p "Enter numbers to DELETE, comma-separated (e.g. 0,2,3), 'all', or blank to abort: " reply
  if [[ -z "$reply" ]]; then
    return 0
  fi
  if [[ "$(echo "$reply" | tr '[:upper:]' '[:lower:]' | xargs)" == "all" ]]; then
    printf '%s\n' "${ids[@]}"
    return 0
  fi
  IFS=',' read -ra indices <<< "$reply"
  for idx in "${indices[@]}"; do
    idx="$(echo "$idx" | xargs)"
    if [[ "$idx" =~ ^[0-9]+$ ]] && (( idx >= 0 && idx < ${#ids[@]} )); then
      echo "${ids[$idx]}"
    fi
  done
}

if [[ -n "$RESOURCE_GROUP" ]]; then
  echo "==> Looking for deployment '${DEPLOYMENT_NAME}' in '${RESOURCE_GROUP}' and its resourceManifest output ..."
  manifest_json="$(az deployment group show --resource-group "$RESOURCE_GROUP" --name "$DEPLOYMENT_NAME" --query "properties.outputs.resourceManifest.value" -o json 2>/dev/null || true)"

  manifest_ids=()
  if [[ -n "$manifest_json" && "$manifest_json" != "null" ]]; then
    mapfile -t manifest_ids < <(echo "$manifest_json" | python3 -c "import json,sys; [print(x) for x in json.load(sys.stdin)]" 2>/dev/null || true)
  fi

  # Belt-and-suspenders: never delete a disk snapshot through this script, even if one somehow
  # ended up in a manifest. Snapshots are how backups (e.g. of the pre-existing VM stack sharing
  # this resource group) are protected - this script must never be the thing that deletes them.
  excluded_snapshots=()
  kept_ids=()
  for id in "${manifest_ids[@]}"; do
    if [[ "$id" == *"/providers/Microsoft.Compute/snapshots/"* ]]; then
      excluded_snapshots+=("$id")
    else
      kept_ids+=("$id")
    fi
  done
  manifest_ids=("${kept_ids[@]}")
  if [[ ${#excluded_snapshots[@]} -gt 0 ]]; then
    echo "Excluded ${#excluded_snapshots[@]} snapshot resource(s) from deletion (snapshots are never deleted by this script):"
    printf '  %s\n' "${excluded_snapshots[@]}"
  fi

  if [[ ${#manifest_ids[@]} -gt 0 ]]; then
    echo "Found a resource manifest with ${#manifest_ids[@]} resource(s) (deletion-safe order):"
    printf '  %s\n' "${manifest_ids[@]}"

    if [[ "$WHAT_IF" == "true" ]]; then
      echo ""
      echo "-w (WhatIf): nothing deleted. Re-run without -w to delete exactly the ${#manifest_ids[@]} resource(s) above."
      exit 0
    fi

    if [[ "$ASSUME_YES" != "true" ]]; then
      read -r -p "Delete exactly these ${#manifest_ids[@]} resource(s)? [y/N] " reply
      [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
    fi

    echo "==> az resource delete --ids ... (all ${#manifest_ids[@]} from the manifest)"
    az resource delete --ids "${manifest_ids[@]}"
    echo "Done. ${#manifest_ids[@]} resource(s) deleted; the resource group itself was left in place."
    exit 0
  fi

  echo "No resource manifest found for deployment '${DEPLOYMENT_NAME}' in '${RESOURCE_GROUP}' (older deployment, wrong -d name, or history purged)."

  # Hard safety gate: never fall back to deleting the entire resource group if it contains any
  # disk snapshots - those are backups (see azure-backups/backup-vm-resources.ps1) and this
  # script must never be able to wipe them out, regardless of -y/-w. There is no flag to bypass
  # this - move or delete the snapshot deliberately first if a full group delete is truly intended.
  existing_snapshots="$(az resource list -g "$RESOURCE_GROUP" --resource-type Microsoft.Compute/snapshots --query "[].id" -o tsv 2>/dev/null || true)"
  if [[ -n "$existing_snapshots" ]]; then
    echo "" >&2
    echo "Refusing to fall back to 'az group delete' for '${RESOURCE_GROUP}' - it contains disk snapshot(s), which are backups:" >&2
    echo "$existing_snapshots" >&2
    echo "" >&2
    echo "Use -e instead for a precise, tag-scoped cleanup that skips these automatically." >&2
    exit 1
  fi

  if [[ "$WHAT_IF" == "true" ]]; then
    echo "-w (WhatIf): no manifest to preview. Falling back would delete the ENTIRE resource group '${RESOURCE_GROUP}' and everything in it - re-run without -w only if that's really what you want."
    exit 0
  fi
  echo "Falling back to deleting the ENTIRE resource group '${RESOURCE_GROUP}' and everything in it."
  if [[ "$ASSUME_YES" != "true" ]]; then
    read -r -p "Continue? [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
  fi
  echo "==> az group delete --name ${RESOURCE_GROUP}"
  az group delete --name "$RESOURCE_GROUP" --yes --no-wait
  echo "Deletion started (--no-wait) — check 'az group show --name ${RESOURCE_GROUP}' until it 404s to confirm it's fully gone."
else
  echo "==> Finding resources tagged Environment=${ENVIRONMENT_TAG} AND ManagedBy=Bicep ..."
  # az CLI's --tag only accepts one key=value filter at a time, so ManagedBy=Bicep is applied
  # client-side via --query instead of a second --tag flag.
  mapfile -t ids < <(az resource list --tag "Environment=${ENVIRONMENT_TAG}" --query "[?tags.ManagedBy=='Bicep'].id" -o tsv)

  # Belt-and-suspenders: same snapshot exclusion as the manifest path above - a snapshot would
  # only reach here if someone manually tagged it Environment=.../ManagedBy=Bicep, but this
  # script must never be the thing that deletes a backup snapshot regardless.
  excluded_snapshots=()
  kept_ids=()
  for id in "${ids[@]}"; do
    if [[ "$id" == *"/providers/Microsoft.Compute/snapshots/"* ]]; then
      excluded_snapshots+=("$id")
    else
      kept_ids+=("$id")
    fi
  done
  ids=("${kept_ids[@]}")
  if [[ ${#excluded_snapshots[@]} -gt 0 ]]; then
    echo "Excluded ${#excluded_snapshots[@]} snapshot resource(s) from deletion (snapshots are never deleted by this script):"
    printf '  %s\n' "${excluded_snapshots[@]}"
  fi

  if [[ ${#ids[@]} -eq 0 ]]; then
    echo "No resources found tagged Environment=${ENVIRONMENT_TAG} and ManagedBy=Bicep. Nothing to do."
    exit 0
  fi

  echo "Found ${#ids[@]} resource(s):"
  printf '  %s\n' "${ids[@]}"

  if [[ "$WHAT_IF" == "true" ]]; then
    echo ""
    echo "-w (WhatIf): nothing deleted. Re-run without -w to pick which of the ${#ids[@]} resource(s) above to delete."
    exit 0
  fi

  to_delete=()
  if [[ "$ASSUME_YES" == "true" ]]; then
    to_delete=("${ids[@]}")
  else
    mapfile -t to_delete < <(pick_resources "${ids[@]}")
  fi
  if [[ ${#to_delete[@]} -eq 0 ]]; then
    echo "Aborted - nothing selected."
    exit 1
  fi

  echo "==> az resource delete --ids ... (${#to_delete[@]} of ${#ids[@]} selected)"
  az resource delete --ids "${to_delete[@]}"
  echo "Done. ${#to_delete[@]} resource(s) deleted."
fi
