#!/usr/bin/env bash
# Same behaviour as manage-custom-domain.ps1 — Segue/FHIRBridge custom domain + managed SSL.
# See that file's header for Mitul's Info → DNS → Add → Bind flow and Bicep two-phase notes.
set -euo pipefail

RESOURCE_GROUP=""
APP_NAME=""
ENVIRONMENT_NAME=""
DOMAIN=""
ACTION=""
VALIDATION_METHOD="CNAME"
WAIT_TIMEOUT_MINUTES=30
WAIT_POLL_SECONDS=30

usage() { sed -n '2,8p' "$0" | tr -d '#'; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --resource-group|-g) RESOURCE_GROUP="$2"; shift 2 ;;
    --app-name|-n) APP_NAME="$2"; shift 2 ;;
    --environment-name|-e) ENVIRONMENT_NAME="$2"; shift 2 ;;
    --domain|-d) DOMAIN="$2"; shift 2 ;;
    --action|-a) ACTION="$2"; shift 2 ;;
    --validation-method|-v) VALIDATION_METHOD="$2"; shift 2 ;;
    --wait-timeout-minutes) WAIT_TIMEOUT_MINUTES="$2"; shift 2 ;;
    --wait-poll-seconds) WAIT_POLL_SECONDS="$2"; shift 2 ;;
    -h|--help) usage ;;
    *) echo "Unknown arg: $1"; usage ;;
  esac
done

[[ -n "$RESOURCE_GROUP" && -n "$APP_NAME" && -n "$ENVIRONMENT_NAME" && -n "$DOMAIN" && -n "$ACTION" ]] || usage

assert_az() {
  command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) not found."; exit 1; }
  az account show >/dev/null 2>&1 || { echo "Not logged in. Run: az login"; exit 1; }
}

get_fqdn() {
  az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query "properties.configuration.ingress.fqdn" -o tsv
}

get_verification_id() {
  az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query "properties.customDomainVerificationId" -o tsv
}

show_info() {
  local fqdn verification_id
  fqdn="$(get_fqdn)"
  verification_id="$(get_verification_id)"
  cat <<EOF

=== Custom domain DNS instructions ===
App:            ${APP_NAME}
Resource group: ${RESOURCE_GROUP}
Environment:    ${ENVIRONMENT_NAME}
Custom domain:  ${DOMAIN}
Default FQDN:   ${fqdn}

Create at your DNS provider:
  CNAME  ${DOMAIN}           -> ${fqdn}
  TXT    asuid.${DOMAIN}     -> ${verification_id}

Then: -a Wait (optional) then -a Both
Or Bicep Phase 2: redeploy with bindCustomDomainCertificates=true

EOF
}

dns_ready() {
  local fqdn verification_id cname_val txt_val
  fqdn="$(get_fqdn)"
  verification_id="$(get_verification_id)"
  cname_val="$(dig +short CNAME "$DOMAIN" 2>/dev/null | tr -d '\n' || true)"
  txt_val="$(dig +short TXT "asuid.${DOMAIN}" 2>/dev/null | tr -d '"\n' || true)"
  echo "CNAME ${DOMAIN} -> ${cname_val:-"(not found)"}"
  echo "TXT asuid.${DOMAIN} -> ${txt_val:-"(not found)"}"
  [[ "$cname_val" == *"${fqdn}"* ]] || return 1
  [[ "$txt_val" == *"${verification_id}"* ]] || return 1
}

invoke_wait() {
  local deadline now
  echo "Waiting up to ${WAIT_TIMEOUT_MINUTES} min for DNS..."
  deadline=$(( $(date +%s) + WAIT_TIMEOUT_MINUTES * 60 ))
  while true; do
    now=$(date +%s)
    (( now >= deadline )) && { echo "Timed out waiting for DNS."; exit 1; }
    if dns_ready; then echo "DNS looks ready."; return 0; fi
    sleep "$WAIT_POLL_SECONDS"
  done
}

invoke_add() {
  echo "Adding hostname '${DOMAIN}'..."
  az containerapp hostname add --hostname "$DOMAIN" -n "$APP_NAME" -g "$RESOURCE_GROUP"
}

invoke_bind() {
  echo "Binding managed certificate for '${DOMAIN}'..."
  az containerapp hostname bind --hostname "$DOMAIN" -n "$APP_NAME" -g "$RESOURCE_GROUP" \
    --environment "$ENVIRONMENT_NAME" --validation-method "$VALIDATION_METHOD"
  az containerapp env certificate list -n "$ENVIRONMENT_NAME" -g "$RESOURCE_GROUP" \
    --query "[].{Name:name, State:properties.provisioningState, Subject:properties.subjectName}" -o table
}

show_list() {
  az containerapp hostname list -n "$APP_NAME" -g "$RESOURCE_GROUP" -o table
  az containerapp env certificate list -n "$ENVIRONMENT_NAME" -g "$RESOURCE_GROUP" \
    --query "[].{Name:name, State:properties.provisioningState, Subject:properties.subjectName}" -o table
}

invoke_delete() {
  az containerapp hostname delete --hostname "$DOMAIN" -n "$APP_NAME" -g "$RESOURCE_GROUP" --yes
}

assert_az
case "$ACTION" in
  Info) show_info ;;
  Wait) invoke_wait ;;
  Add) invoke_add ;;
  Bind) invoke_bind ;;
  Both) invoke_add; invoke_bind ;;
  List) show_list ;;
  Delete) invoke_delete ;;
  *) echo "Invalid --action: $ACTION"; exit 1 ;;
esac
