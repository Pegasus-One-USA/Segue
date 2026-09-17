#!/usr/bin/env bash
# Automates the custom-domain.bicep flow end to end: reads each app's default FQDN + domain
# verification ID directly from the live container app (no deploy needed for this - main.bicep
# already assigns it the moment the app exists), prints the CNAME + asuid TXT records to create,
# polls DNS until they resolve, then deploys custom-domain.bicep ONCE - it registers the
# hostname, creates the certificate, and binds it, all in that one deploy.
#
# Usage:
#   ./auto-bind-custom-domain.sh -g rg-tusharpuri -p segue13 -a segueapp.pegasusone.com
#
# Safe to re-run: custom-domain.bicep is idempotent either way (a domain that's already bound is
# just re-affirmed, not disturbed in any lasting way).
set -euo pipefail

RESOURCE_GROUP=""
NAME_PREFIX=""
SEGUE_APP_DOMAIN=""
WAIT_TIMEOUT_MINUTES=30
WAIT_POLL_SECONDS=30

while getopts "g:p:a:w:s:" opt; do
    case $opt in
        g) RESOURCE_GROUP="$OPTARG" ;;
        p) NAME_PREFIX="$OPTARG" ;;
        a) SEGUE_APP_DOMAIN="$OPTARG" ;;
        w) WAIT_TIMEOUT_MINUTES="$OPTARG" ;;
        s) WAIT_POLL_SECONDS="$OPTARG" ;;
        *) echo "Usage: $0 -g <resource-group> -p <name-prefix> [-a <segue-app-domain>] [-w <wait-timeout-min>] [-s <poll-interval-sec>]" >&2; exit 1 ;;
    esac
done

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BICEP_FILE="$HERE/../azure-deploy/custom-domain.bicep"

command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) not found. Install it, then run 'az login'." >&2; exit 1; }
[[ -f "$BICEP_FILE" ]] || { echo "Could not find custom-domain.bicep at $BICEP_FILE" >&2; exit 1; }
[[ -n "$RESOURCE_GROUP" && -n "$NAME_PREFIX" ]] || { echo "Both -g <resource-group> and -p <name-prefix> are required." >&2; exit 1; }
if [[ -z "$SEGUE_APP_DOMAIN" ]]; then
    echo "Set -a <segue-app-domain>." >&2
    exit 1
fi

dns_ready() {
    local domain="$1" fqdn="$2" verification_id="$3"
    local cname_ok=false txt_ok=false
    if command -v dig >/dev/null 2>&1; then
        local cname_target
        cname_target=$(dig +short CNAME "$domain" 2>/dev/null | sed 's/\.$//')
        [[ "$cname_target" == "${fqdn%.}" ]] && cname_ok=true
        local txt_value
        txt_value=$(dig +short TXT "asuid.$domain" 2>/dev/null)
        [[ "$txt_value" == *"$verification_id"* ]] && txt_ok=true
    else
        local nslookup_out
        nslookup_out=$(nslookup "$domain" 2>/dev/null || true)
        [[ "$nslookup_out" == *"${fqdn%.}"* ]] && cname_ok=true
        nslookup_out=$(nslookup -type=TXT "asuid.$domain" 2>/dev/null || true)
        [[ "$nslookup_out" == *"$verification_id"* ]] && txt_ok=true
    fi
    [[ "$cname_ok" == true && "$txt_ok" == true ]]
}

# --- Read each requested app's current FQDN + verification ID directly - no deploy needed, these
# already exist the moment main.bicep created the app. ---
echo "==> Reading app details ..."
pending_labels=()
pending_appnames=()
pending_domains=()
pending_fqdns=()
pending_verification_ids=()

declare -A LABEL_TO_APPNAME=( [segueApp]="${NAME_PREFIX}-app" )
declare -A LABEL_TO_DOMAIN=( [segueApp]="$SEGUE_APP_DOMAIN" )

for label in segueApp; do
    domain="${LABEL_TO_DOMAIN[$label]}"
    [[ -z "$domain" ]] && continue
    app_name="${LABEL_TO_APPNAME[$label]}"

    show=$(az containerapp show --name "$app_name" --resource-group "$RESOURCE_GROUP" -o json 2>/dev/null) || true
    if [[ -z "$show" ]]; then
        echo "Could not find app '$app_name' in '$RESOURCE_GROUP' - has Step 1 finished?" >&2
        exit 1
    fi
    fqdn=$(echo "$show" | python3 -c "import sys,json; print(json.load(sys.stdin)['properties']['configuration']['ingress']['fqdn'])")
    verification_id=$(echo "$show" | python3 -c "import sys,json; print(json.load(sys.stdin)['properties']['customDomainVerificationId'])")
    already_bound=$(echo "$show" | python3 -c "
import sys, json
obj = json.load(sys.stdin)
domains = obj['properties']['configuration']['ingress'].get('customDomains') or []
print('yes' if any(d['name'] == '$domain' and d.get('bindingType') == 'SniEnabled' for d in domains) else 'no')
")

    echo ""
    echo "=== ${label} (${app_name}) - ${domain} ==="
    if [[ "$already_bound" == "yes" ]]; then
        echo "  Already certificate-bound. Live at https://${domain}"
        continue
    fi
    echo "  CNAME  ${domain}          ->  ${fqdn}"
    echo "  TXT    asuid.${domain}    ->  ${verification_id}"
    pending_labels+=("$label")
    pending_appnames+=("$app_name")
    pending_domains+=("$domain")
    pending_fqdns+=("$fqdn")
    pending_verification_ids+=("$verification_id")
done

if [[ ${#pending_domains[@]} -eq 0 ]]; then
    echo ""
    echo "All requested domain(s) already have a bound certificate. Nothing further to do."
    exit 0
fi

echo ""
echo "==> Create the record(s) above at your DNS provider now."
echo "==> Polling DNS for ${#pending_domains[@]} domain(s) (up to ${WAIT_TIMEOUT_MINUTES} min) ..."
deadline=$(( $(date +%s) + WAIT_TIMEOUT_MINUTES * 60 ))
while true; do
    still_pending=()
    for i in "${!pending_domains[@]}"; do
        if ! dns_ready "${pending_domains[$i]}" "${pending_fqdns[$i]}" "${pending_verification_ids[$i]}"; then
            still_pending+=("${pending_domains[$i]}")
        fi
    done
    if [[ ${#still_pending[@]} -eq 0 ]]; then
        echo "DNS looks ready for all pending domain(s)."
        break
    fi
    if [[ $(date +%s) -ge $deadline ]]; then
        echo "Timed out after ${WAIT_TIMEOUT_MINUTES} min waiting for DNS on: ${still_pending[*]}. Re-run this script once DNS is confirmed propagated (also check CAA records)." >&2
        exit 1
    fi
    echo "  Still waiting on: ${still_pending[*]} ..."
    sleep "$WAIT_POLL_SECONDS"
done

# --- One deploy: custom-domain.bicep registers the hostname, creates the certificate, and binds
# it, all in this same deploy, now that DNS is ready. ---
echo ""
echo "==> Deploying custom-domain.bicep (namePrefix=${NAME_PREFIX}) ..."
params=("namePrefix=${NAME_PREFIX}")
[[ -n "$SEGUE_APP_DOMAIN" ]] && params+=("segueAppDomain=${SEGUE_APP_DOMAIN}")

results_json=$(az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$BICEP_FILE" \
    --parameters "${params[@]}" \
    --query "properties.outputs.results.value" -o json)

echo ""
count=$(echo "$results_json" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))")
for i in $(seq 0 $((count - 1))); do
    entry=$(echo "$results_json" | python3 -c "import sys,json; print(json.dumps(json.load(sys.stdin)[$i]))")
    app=$(echo "$entry" | python3 -c "import sys,json; print(json.load(sys.stdin)['app'])")
    custom_url=$(echo "$entry" | python3 -c "import sys,json; print(json.load(sys.stdin)['customDomainUrl'])")
    echo "${app} is now live at ${custom_url}"
done
