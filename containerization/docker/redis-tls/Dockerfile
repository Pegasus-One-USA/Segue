# Redis, with TLS enabled using a fixed, committed self-signed certificate.
#
# Why this image exists at all: FHIRBridge.Infrastructure's DependencyInjection.cs refuses to
# start outside Development unless ConnectionStrings:Redis contains "ssl=true" (HIPAA #15 — no
# plaintext Redis outside dev). Stock redis:7-alpine has no TLS configured, so every containerized
# deployment (Bicep, and the Terraform azure/aws/local environments) needs Redis itself to
# actually terminate TLS, not just a connection-string flag the server can't honor.
#
# The cert is intentionally FIXED and committed (redis.crt/redis.key, generated once via
# `openssl req -x509 -newkey rsa:2048 -nodes -days 3650 ...` — see this folder's own history/the
# exact command in the PR that added it), not regenerated per build or per deploy. That's what
# lets FHIRBridge.Infrastructure pin against one known certificate thumbprint
# (RedisTlsOptions.TrustedCertificateThumbprint in DependencyInjection.cs) instead of either (a)
# blindly accepting any certificate presented at the Redis hostname — which would defeat the
# point of validating anything — or (b) needing a way to plumb a freshly-generated cert's
# thumbprint into the app at deploy time, which Bicep/ARM has no native mechanism for (unlike
# Terraform's tls_private_key/tls_self_signed_cert resources, which only cover 3 of the 4
# environments this needs to work in).
#
# This is intentionally a private, internal-traffic-only certificate — it never faces the
# internet (Redis itself is always internal-only, reached only by fhirbridge-app/worker within
# the same private network/environment) — so a committed, non-rotating self-signed cert is an
# acceptable trade-off here, unlike the ALB's own self-signed cert (main.tf, aws environment),
# which is documented as "swap for a real ACM certificate" specifically because that one fronts
# real external traffic.
#
# If this certificate is ever regenerated, RedisTlsOptions.TrustedCertificateThumbprint in
# src/FHIRBridge.Infrastructure/DependencyInjection.cs MUST be updated to match — the app will
# refuse every Redis connection otherwise (fails closed, not open, by design).
FROM redis:7-alpine

# Paths are repo-root-relative because build-images.ps1/.sh build every image (including this one)
# from the repo root as context, for consistency with the other 3 images (which genuinely need
# repo-root context to reach src/, portal/, Demo_TestApp/).
COPY containerization/docker/redis-tls/redis.crt /certs/redis.crt
COPY containerization/docker/redis-tls/redis.key /certs/redis.key

# No custom ENTRYPOINT/CMD here — every environment already passes its own full `command` array
# to set --requirepass; that command now also adds the --tls-* flags (see main.bicep /
# terraform environments' redis resources). Keeping this Dockerfile to just "add the cert files"
# means the existing --requirepass-based command construction didn't need restructuring, only
# extending.
