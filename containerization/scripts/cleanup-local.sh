#!/usr/bin/env bash
# Tears down the local Segue containerized stack — either the docker-compose path (default)
# or the Terraform local environment (-t), so nothing keeps running/consuming disk after testing.
#
# Usage:
#   ./cleanup-local.sh              # docker compose down -v (containers + volumes + network)
#   ./cleanup-local.sh -k           # docker compose down, keep volumes (data survives)
#   ./cleanup-local.sh -t           # terraform destroy in terraform/environments/local instead
#   ./cleanup-local.sh -y           # skip the confirmation prompt
set -euo pipefail

USE_TERRAFORM="false"
KEEP_VOLUMES="false"
ASSUME_YES="false"

while getopts "tky" opt; do
  case "$opt" in
    t) USE_TERRAFORM="true" ;;
    k) KEEP_VOLUMES="true" ;;
    y) ASSUME_YES="true" ;;
    *) echo "Usage: $0 [-t] [-k] [-y]" >&2; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if [[ "$ASSUME_YES" != "true" ]]; then
  read -r -p "This removes every Segue local container (and volumes, unless -k) — continue? [y/N] " reply
  [[ "$reply" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }
fi

if [[ "$USE_TERRAFORM" == "true" ]]; then
  echo "==> terraform destroy (terraform/environments/local)"
  cd "${REPO_ROOT}/containerization/terraform/environments/local"
  terraform destroy -auto-approve
else
  echo "==> docker compose down $( [[ "$KEEP_VOLUMES" == "true" ]] && echo "" || echo "-v" )"
  cd "${REPO_ROOT}/containerization/compose"
  if [[ "$KEEP_VOLUMES" == "true" ]]; then
    docker compose down
  else
    docker compose down -v
  fi
fi

echo "Done. Local Segue stack removed."
