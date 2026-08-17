#!/bin/bash
# Adds (and/or binds a free managed certificate for) a custom domain on an already-deployed
# Container App, directly via the Container Apps CLI -- no Terraform apply / Bicep-ARM redeploy
# needed for either step. This is the genuine 2-step split Azure itself supports
# (`az containerapp hostname add` then `az containerapp hostname bind`), used here instead of going
# through the Terraform environment's *_custom_domain/*_ssl_enabled variables or main.bicep's
# fhirbridgeAppCustomDomain/SslEnabled parameters + a full apply/redeploy each time. Works
# identically against a Container App regardless of which IaC tool created it -- these are plain
# `az containerapp` calls against the resource itself, nothing Terraform- or Bicep-specific.
#
# CAVEAT -- both the Terraform environment and main.bicep also manage each app's customDomains
# declaratively. If you use this script to add/bind a domain and then LATER `terraform apply` /
# redeploy the wizard with that app's custom-domain variable/parameter left blank, that apply will
# remove what this script added (both tools reconcile the full declared state on every run). Once
# you start managing a domain with this script, either keep the matching variable/parameter filled
# in on every future apply/redeploy too, or manage that domain exclusively through this script from
# then on -- don't mix the two for the same app.
#
# Usage:
#   # Step 1: just print the CNAME target + TXT verification value you need to add at your DNS
#   # provider -- makes no changes.
#   ./manage-custom-domain.sh -g rg-tusharpuri -n segue5-app -e segue5-env -d app.example.com -a info
#
#   # Step 2: once those DNS records resolve, register the domain (no certificate yet).
#   ./manage-custom-domain.sh -g rg-tusharpuri -n segue5-app -e segue5-env -d app.example.com -a add
#
#   # Step 3: once the TXT record is confirmed, issue + bind the free managed certificate.
#   ./manage-custom-domain.sh -g rg-tusharpuri -n segue5-app -e segue5-env -d app.example.com -a bind
#
#   # Or do steps 2+3 in one call, if DNS is already fully in place:
#   ./manage-custom-domain.sh -g rg-tusharpuri -n segue5-app -e segue5-env -d app.example.com -a both
set -e

VALIDATION_METHOD="TXT"

usage() {
  echo "Usage: $0 -g <resource-group> -n <app-name> -e <environment-name> -d <domain> -a <info|add|bind|both> [-m <CNAME|HTTP|TXT>]"
  exit 1
}

while getopts "g:n:e:d:a:m:" opt; do
  case "$opt" in
    g) RESOURCE_GROUP="$OPTARG" ;;
    n) APP_NAME="$OPTARG" ;;
    e) ENVIRONMENT_NAME="$OPTARG" ;;
    d) DOMAIN="$OPTARG" ;;
    a) ACTION="$OPTARG" ;;
    m) VALIDATION_METHOD="$OPTARG" ;;
    *) usage ;;
  esac
done

if [ -z "$RESOURCE_GROUP" ] || [ -z "$APP_NAME" ] || [ -z "$ENVIRONMENT_NAME" ] || [ -z "$DOMAIN" ] || [ -z "$ACTION" ]; then
  usage
fi

echo "== $APP_NAME's current default hostname + domain verification code =="
FQDN=$(az containerapp show --name "$APP_NAME" --resource-group "$RESOURCE_GROUP" --query "properties.configuration.ingress.fqdn" -o tsv)
VERIFICATION_ID=$(az containerapp show --name "$APP_NAME" --resource-group "$RESOURCE_GROUP" --query "properties.customDomainVerificationId" -o tsv)
echo "  CNAME: $DOMAIN -> $FQDN"
echo "  TXT:   asuid.$DOMAIN -> $VERIFICATION_ID"
echo ""

if [ "$ACTION" = "info" ]; then
  echo "Add both records above at your DNS provider, confirm they resolve, then re-run with -a add."
  exit 0
fi

echo "Make sure both DNS records above are already in place and resolving before continuing (dig CNAME / dig TXT) -- this will fail with a clear error if they aren't."
echo ""

if [ "$ACTION" = "add" ] || [ "$ACTION" = "both" ]; then
  echo "== Registering $DOMAIN on $APP_NAME (no certificate yet) =="
  az containerapp hostname add --hostname "$DOMAIN" --resource-group "$RESOURCE_GROUP" --name "$APP_NAME"
fi

if [ "$ACTION" = "bind" ] || [ "$ACTION" = "both" ]; then
  echo "== Issuing + binding a free managed certificate for $DOMAIN (validation: $VALIDATION_METHOD) =="
  az containerapp hostname bind --hostname "$DOMAIN" --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" --environment "$ENVIRONMENT_NAME" --validation-method "$VALIDATION_METHOD"
fi

echo ""
echo "== Current custom domains on $APP_NAME =="
az containerapp hostname list --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" -o table
