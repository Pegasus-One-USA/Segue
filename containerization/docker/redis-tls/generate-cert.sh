#!/usr/bin/env bash
# Generates a fresh self-signed cert+key pair for the containerized Redis's TLS listener.
#
# Deliberately NOT committed to source control (redis.crt/redis.key are gitignored) -- run this
# once per deployment/release before building images, then plug the printed thumbprint into
# whichever environment you're deploying to (Bicep's redisTrustedCertificateThumbprint parameter,
# or the matching Terraform variable in each of azure/aws/local). FHIRBridge.Infrastructure's
# DependencyInjection.cs only trusts a self-signed Redis certificate whose thumbprint matches that
# configured value (see ValidateRedisServerCertificate) -- it fails closed otherwise, so a missed
# or mismatched thumbprint means the app refuses to start, not a silently-insecure connection.
#
# Usage:
#   ./generate-cert.sh          # generates (or reports the existing) redis.crt/redis.key
#   ./generate-cert.sh -f       # regenerates even if a cert+key pair already exists
set -euo pipefail

FORCE="false"
while getopts "f" opt; do
  case "$opt" in
    f) FORCE="true" ;;
    *) echo "Usage: $0 [-f]" >&2; exit 1 ;;
  esac
done

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CRT_PATH="${HERE}/redis.crt"
KEY_PATH="${HERE}/redis.key"

if [[ -f "$CRT_PATH" && -f "$KEY_PATH" && "$FORCE" != "true" ]]; then
  echo "redis.crt/redis.key already exist here - leaving them as-is (pass -f to regenerate)."
else
  command -v openssl >/dev/null 2>&1 || { echo "openssl not found on PATH. Install it and re-run." >&2; exit 1; }

  echo "==> Generating a new self-signed certificate for Redis TLS ..."
  openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$KEY_PATH" -out "$CRT_PATH" \
    -days 3650 \
    -subj "/CN=fhirbridge-redis/O=FHIRBridge/OU=containerization" \
    -addext "subjectAltName=DNS:redis,DNS:*.internal,DNS:localhost"
fi

THUMBPRINT="$(openssl x509 -in "$CRT_PATH" -noout -fingerprint -sha1 | sed -E 's/^.*=//; s/://g')"
echo
echo "Certificate thumbprint (SHA-1): ${THUMBPRINT}"
echo
echo "Set this as:"
echo "  - Bicep:                       redisTrustedCertificateThumbprint parameter"
echo "  - Terraform (azure/aws/local): redis_trusted_certificate_thumbprint variable"
echo
echo "Then build/push images as usual (containerization/scripts/build-images.sh|ps1) -"
echo "the fhirbridge-redis image bakes in whatever redis.crt/redis.key currently sit in this folder."
