#!/usr/bin/env bash
# Tears down a Bicep (main.bicep) deployment. Unlike the Terraform environments, this path has no
# state file to destroy from — cleanup instead relies on the resource group being dedicated to
# this deployment (recommended, see README.md's Tier 1 flow), or on the commonTags every
# taggable resource in main.bicep carries (Project/Component/Environment/ManagedBy) if you
# deployed into a shared resource group instead.
#
# Usage:
#   ./cleanup.sh -g fhirbridge-rg          # delete the whole resource group (recommended, fastest, complete)
#   ./cleanup.sh -e fhirbridge             # tag-based: delete only resources tagged Environment=<value>
#   ./cleanup.sh -g fhirbridge-rg -y       # skip the confirmation prompt
set -euo pipefail

RESOURCE_GROUP=""
ENVIRONMENT_TAG=""
ASSUME_YES="false"

while getopts "g:e:y" opt; do
  case "$opt" in
    g) RESOURCE_GROUP="$OPTARG" ;;
    e) ENVIRONMENT_TAG="$OPTARG" ;;
    y) ASSUME_YES="true" ;;
    *) echo "Usage: $0 [-g resource-group] [-e environment-tag-value] [-y]" >&2; exit 1 ;;
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

if [[ -n "$RESOURCE_GROUP" ]]; then
  echo "This will delete the ENTIRE resource group '${RESOURCE_GROUP}' and everything in it."
  if [[ "$ASSUME_YES" != "true" ]]; then
    read -r -p "Continue? [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
  fi
  echo "==> az group delete --name ${RESOURCE_GROUP}"
  az group delete --name "$RESOURCE_GROUP" --yes --no-wait
  echo "Deletion started (--no-wait) — check 'az group show --name ${RESOURCE_GROUP}' until it 404s to confirm it's fully gone."
else
  echo "==> Finding resources tagged Environment=${ENVIRONMENT_TAG} ..."
  mapfile -t ids < <(az resource list --tag "Environment=${ENVIRONMENT_TAG}" --query "[].id" -o tsv)

  if [[ ${#ids[@]} -eq 0 ]]; then
    echo "No resources found tagged Environment=${ENVIRONMENT_TAG}. Nothing to do."
    exit 0
  fi

  echo "Found ${#ids[@]} resource(s):"
  printf '  %s\n' "${ids[@]}"

  if [[ "$ASSUME_YES" != "true" ]]; then
    read -r -p "Delete all ${#ids[@]} of these? [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
  fi

  echo "==> az resource delete --ids ..."
  az resource delete --ids "${ids[@]}"
  echo "Done. ${#ids[@]} tagged resource(s) deleted."
fi
