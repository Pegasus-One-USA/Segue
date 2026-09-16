#!/usr/bin/env bash
# Read-only discovery helper for the Path A custom-domain flow (custom-domain.bicep - see
# containerization/azure-deploy/CUSTOM_DOMAIN_SELF_SERVICE.md). Finds the public-facing app for
# a given name prefix (Segue app), shows its default URL and
# Azure-assigned domain-verification ID, and - once you type in a domain for an app - prints
# the exact CNAME + TXT records to create at your DNS provider, plus ready-to-run
# custom-domain.bicep deploy commands for both phases (hostname-only, then certificate-bind).
#
# This makes NO changes itself - every call here is a read-only `az containerapp show`. Actual
# binding happens when you run the printed custom-domain.bicep commands (or the equivalent
# Deploy-to-Azure link) yourself.
#
# Usage:
#   ./discover-custom-domains.sh                                    # prompts for both values
#   ./discover-custom-domains.sh rg-tusharpuri segue12
set -euo pipefail

RESOURCE_GROUP="${1:-}"
NAME_PREFIX="${2:-}"

if [[ -z "$RESOURCE_GROUP" ]]; then
    read -r -p "Resource group: " RESOURCE_GROUP
fi
if [[ -z "$NAME_PREFIX" ]]; then
    read -r -p "Name prefix (e.g. segue12): " NAME_PREFIX
fi

# the database container/redis/worker are never eligible - they have no public ingress, so no
# custom domain is possible for them.
LABELS=("Segue app")
APP_NAMES=("${NAME_PREFIX}-app")

echo ""
echo "==> Looking for '${NAME_PREFIX}'-prefixed apps in '${RESOURCE_GROUP}' ..."
FOUND_LABELS=()
FOUND_APP_NAMES=()
FOUND_FQDNS=()
FOUND_VERIFICATION_IDS=()

for i in "${!APP_NAMES[@]}"; do
    label="${LABELS[$i]}"
    app_name="${APP_NAMES[$i]}"
    show=$(az containerapp show --name "$app_name" --resource-group "$RESOURCE_GROUP" -o json 2>/dev/null) || true
    if [[ -z "$show" ]]; then
        echo "  - ${label} (${app_name}): not found - skipping."
        continue
    fi
    fqdn=$(echo "$show" | python3 -c "import sys,json; print(json.load(sys.stdin)['properties']['configuration']['ingress']['fqdn'])")
    verification_id=$(echo "$show" | python3 -c "import sys,json; print(json.load(sys.stdin)['properties']['customDomainVerificationId'])")
    FOUND_LABELS+=("$label")
    FOUND_APP_NAMES+=("$app_name")
    FOUND_FQDNS+=("$fqdn")
    FOUND_VERIFICATION_IDS+=("$verification_id")
done

if [[ ${#FOUND_APP_NAMES[@]} -eq 0 ]]; then
    echo "No apps found for prefix '${NAME_PREFIX}' in '${RESOURCE_GROUP}'. Check the resource group and prefix are exactly right (e.g. 'segue12', not 'Segue12')." >&2
    exit 1
fi

echo ""
echo "Found ${#FOUND_APP_NAMES[@]} app(s):"
for i in "${!FOUND_APP_NAMES[@]}"; do
    echo ""
    echo "=== ${FOUND_LABELS[$i]} (${FOUND_APP_NAMES[$i]}) ==="
    echo "  Default URL:             https://${FOUND_FQDNS[$i]}"
    echo "  Domain verification ID: ${FOUND_VERIFICATION_IDS[$i]}"
done

ENVIRONMENT_NAME="${NAME_PREFIX}-env"

echo ""
echo "Enter a domain per app to see its DNS records and deploy commands (leave blank to skip that app)."
for i in "${!FOUND_APP_NAMES[@]}"; do
    read -r -p "Domain for ${FOUND_LABELS[$i]} (${FOUND_APP_NAMES[$i]}): " domain
    if [[ -z "$domain" ]]; then continue; fi

    app_name="${FOUND_APP_NAMES[$i]}"
    fqdn="${FOUND_FQDNS[$i]}"
    verification_id="${FOUND_VERIFICATION_IDS[$i]}"

    echo ""
    echo "--- DNS records to create for ${domain} (${FOUND_LABELS[$i]}) ---"
    echo "  CNAME  ${domain}          -> ${fqdn}"
    echo "  TXT    asuid.${domain}    -> ${verification_id}"
    echo ""
    echo "Once those records are live and propagated, run (Phase 1 - hostname only, no cert yet):"
    echo "  az deployment group create -g ${RESOURCE_GROUP} -f containerization/azure-deploy/custom-domain.bicep -p environmentName=${ENVIRONMENT_NAME} appName=${app_name} domain=${domain} bindCertificate=false"
    echo ""
    echo "Then (Phase 2 - bind the managed certificate, only after DNS is confirmed propagated):"
    echo "  az deployment group create -g ${RESOURCE_GROUP} -f containerization/azure-deploy/custom-domain.bicep -p environmentName=${ENVIRONMENT_NAME} appName=${app_name} domain=${domain} bindCertificate=true"
    echo ""
done

echo "Done - this script made no changes. Re-run anytime; nothing here is destructive."
