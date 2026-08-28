#!/usr/bin/env bash
# Interactive, guided custom-domain setup for an already-deployed FHIRBridge Container Apps
# environment (Bicep or Terraform, any name prefix). Bash equivalent of
# setup-custom-domains-wizard.ps1 — see that file's header for the full "step 2" rationale
# (domains can never be entered on the very first deploy; Azure only assigns a Container App's
# customDomainVerificationId once the app already exists).
#
# What it does:
#   1. Finds which of this name prefix's public-facing apps actually exist (FHIRBridge app, Demo
#      app, Terminology server — sqlserver/redis/worker/term-db are never eligible for a domain).
#   2. Shows each one's default URL and Azure-assigned domain-verification ID.
#   3. Asks, per app, whether you want a custom domain for it, and if so, what domain.
#   4. Once given a domain, prints the exact CNAME + TXT records to create (delegates to
#      manage-custom-domain.sh -a Info, so the two scripts never drift on how records are shown).
#   5. Waits for you to confirm the records are in place (or auto-polls DNS with --auto-wait), then
#      registers the hostname and binds a free managed SSL certificate (manage-custom-domain.sh -a Both).
#
# Usage:
#   ./setup-custom-domains-wizard.sh -g rg-tusharpuri -p segue10
#   ./setup-custom-domains-wizard.sh -g rg-tusharpuri -p segue10 --auto-wait
set -euo pipefail

RESOURCE_GROUP=""
NAME_PREFIX=""
AUTO_WAIT="false"
WAIT_TIMEOUT_MINUTES=30

while [[ $# -gt 0 ]]; do
  case "$1" in
    -g|--resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
    -p|--name-prefix) NAME_PREFIX="$2"; shift 2 ;;
    --auto-wait) AUTO_WAIT="true"; shift ;;
    --wait-timeout-minutes) WAIT_TIMEOUT_MINUTES="$2"; shift 2 ;;
    -h|--help) echo "Usage: $0 -g <resource-group> -p <name-prefix> [--auto-wait] [--wait-timeout-minutes N]"; exit 1 ;;
    *) echo "Unknown arg: $1"; exit 1 ;;
  esac
done

[[ -n "$RESOURCE_GROUP" && -n "$NAME_PREFIX" ]] || { echo "Usage: $0 -g <resource-group> -p <name-prefix> [--auto-wait]"; exit 1; }

command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) not found. Install it, then run 'az login'." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "jq not found — required for this script (used to parse 'az containerapp show' output)." >&2; exit 1; }

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MANAGE_SCRIPT="${HERE}/manage-custom-domain.sh"
[[ -x "$MANAGE_SCRIPT" || -f "$MANAGE_SCRIPT" ]] || { echo "Could not find manage-custom-domain.sh next to this script at ${MANAGE_SCRIPT}" >&2; exit 1; }

ENVIRONMENT_NAME="${NAME_PREFIX}-env"

# label|app_name|port — FHIRBridge app / Demo app are always external by design (main.bicep never
# gives them an internal-only mode). The terminology server is internal-only by default; this
# script enables external ingress on it automatically the moment you ask for a domain on it.
CANDIDATES=(
  "FHIRBridge app|${NAME_PREFIX}-app|80"
  "Demo app|${NAME_PREFIX}-demo-app|5500"
  "Terminology server|${NAME_PREFIX}-term|8080"
)

echo "==> Looking for '${NAME_PREFIX}'-prefixed apps in '${RESOURCE_GROUP}' ..."

FOUND_LABELS=()
FOUND_NAMES=()
FOUND_PORTS=()
FOUND_FQDNS=()
FOUND_EXTERNAL=()
FOUND_VERIFICATION_IDS=()

for entry in "${CANDIDATES[@]}"; do
  IFS='|' read -r label app_name port <<< "$entry"
  show_json="$(az containerapp show --name "$app_name" --resource-group "$RESOURCE_GROUP" -o json 2>/dev/null || true)"
  if [[ -z "$show_json" ]]; then
    echo "  - ${label} (${app_name}): not found - skipping."
    continue
  fi
  has_ingress="$(echo "$show_json" | jq -r '.properties.configuration.ingress // empty')"
  if [[ -z "$has_ingress" ]]; then
    echo "  - ${label} (${app_name}): has no ingress configured at all - skipping (not eligible for a custom domain)."
    continue
  fi
  fqdn="$(echo "$show_json" | jq -r '.properties.configuration.ingress.fqdn // ""')"
  external="$(echo "$show_json" | jq -r '.properties.configuration.ingress.external')"
  verification_id="$(echo "$show_json" | jq -r '.properties.customDomainVerificationId // ""')"

  FOUND_LABELS+=("$label")
  FOUND_NAMES+=("$app_name")
  FOUND_PORTS+=("$port")
  FOUND_FQDNS+=("$fqdn")
  FOUND_EXTERNAL+=("$external")
  FOUND_VERIFICATION_IDS+=("$verification_id")
done

if [[ ${#FOUND_NAMES[@]} -eq 0 ]]; then
  echo "No eligible apps found for prefix '${NAME_PREFIX}' in '${RESOURCE_GROUP}'. Has step 1's deployment finished?" >&2
  exit 1
fi

echo
echo "Found ${#FOUND_NAMES[@]} app(s):"
for i in "${!FOUND_NAMES[@]}"; do
  echo
  echo "=== ${FOUND_LABELS[$i]} (${FOUND_NAMES[$i]}) ==="
  echo "  Default URL:      https://${FOUND_FQDNS[$i]}"
  echo "  Verification ID:  ${FOUND_VERIFICATION_IDS[$i]}"
  if [[ "${FOUND_EXTERNAL[$i]}" == "true" ]]; then
    echo "  Ingress:          external"
  else
    echo "  Ingress:          internal-only (will be switched to external automatically if you add a custom domain)"
  fi
  existing_hostnames="$(az containerapp hostname list --name "${FOUND_NAMES[$i]}" --resource-group "$RESOURCE_GROUP" --query "[].name" -o tsv 2>/dev/null || true)"
  if [[ -n "$existing_hostnames" ]]; then
    echo "  Already has custom domain(s): ${existing_hostnames}"
  else
    echo "  No custom domain bound yet."
  fi
done

for i in "${!FOUND_NAMES[@]}"; do
  label="${FOUND_LABELS[$i]}"
  app_name="${FOUND_NAMES[$i]}"
  port="${FOUND_PORTS[$i]}"
  external="${FOUND_EXTERNAL[$i]}"

  echo
  read -r -p "Set up a custom domain for ${label} (${app_name})? [y/N] " reply
  [[ "$reply" =~ ^[Yy]$ ]] || continue

  read -r -p "Enter the domain for ${label} (e.g. app.customer.com): " domain
  [[ -n "$domain" ]] || { echo "No domain entered - skipping ${label}."; continue; }

  if [[ "$external" != "true" ]]; then
    echo
    echo "--> ${label} is internal-only - enabling external ingress first (required for any custom domain) ..."
    if ! az containerapp ingress enable --name "$app_name" --resource-group "$RESOURCE_GROUP" --type external --target-port "$port" --transport auto >/dev/null; then
      echo "Could not enable external ingress for ${label} - skipping." >&2
      continue
    fi
  fi

  echo
  echo "--> Showing the exact DNS records to create for ${domain} ..."
  "$MANAGE_SCRIPT" -g "$RESOURCE_GROUP" -n "$app_name" -e "$ENVIRONMENT_NAME" -d "$domain" -a Info

  if [[ "$AUTO_WAIT" == "true" ]]; then
    echo "--> --auto-wait: polling DNS automatically (up to ${WAIT_TIMEOUT_MINUTES} min) ..."
    if ! "$MANAGE_SCRIPT" -g "$RESOURCE_GROUP" -n "$app_name" -e "$ENVIRONMENT_NAME" -d "$domain" -a Wait --wait-timeout-minutes "$WAIT_TIMEOUT_MINUTES"; then
      echo "DNS wait timed out for ${domain} - skipping the link/bind step for ${label}. Re-run this script (or manage-custom-domain.sh -a Both) once DNS is confirmed ready." >&2
      continue
    fi
  else
    read -r -p "Press Enter once you've created both records above and believe DNS has propagated (or Ctrl+C to stop here - nothing has been changed yet for ${label}) " _
  fi

  echo "--> Linking domain + TXT record + certificate for ${label} ..."
  if ! "$MANAGE_SCRIPT" -g "$RESOURCE_GROUP" -n "$app_name" -e "$ENVIRONMENT_NAME" -d "$domain" -a Both; then
    echo "Could not complete ${label} (${domain}) - see the error above. You can retry later with:" >&2
    echo "  ${MANAGE_SCRIPT} -g ${RESOURCE_GROUP} -n ${app_name} -e ${ENVIRONMENT_NAME} -d ${domain} -a Both" >&2
    continue
  fi
  echo "${label} is now live at https://${domain}"
done

echo
echo "Done. Re-run this script anytime to add more domains, or use manage-custom-domain.sh directly (-a List/Delete) for one-off changes."
