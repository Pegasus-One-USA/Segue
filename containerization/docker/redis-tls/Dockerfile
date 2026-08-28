# Redis, with TLS enabled using a locally-generated self-signed certificate.
#
# Why this image exists at all: FHIRBridge.Infrastructure's DependencyInjection.cs refuses to
# start outside Development unless ConnectionStrings:Redis contains "ssl=true" (HIPAA #15 — no
# plaintext Redis outside dev). Stock redis:7-alpine has no TLS configured, so every containerized
# deployment (Bicep, and the Terraform azure/aws/local environments) needs Redis itself to
# actually terminate TLS, not just a connection-string flag the server can't honor.
#
# redis.crt/redis.key are NOT committed to source control (gitignored — see this folder's own
# .gitignore) — run generate-cert.ps1|sh once before building this image, which creates a fresh
# self-signed pair here and prints its SHA-1 thumbprint. Set that thumbprint as
# Redis:TrustedCertificateThumbprint (env var Redis__TrustedCertificateThumbprint) on the
# fhirbridge-app/worker deployments that will connect to THIS built image — see
# ValidateRedisServerCertificate in src/FHIRBridge.Infrastructure/DependencyInjection.cs, which
# fails closed (refuses the connection) if the presented certificate's thumbprint doesn't match
# whatever value was configured, rather than blindly accepting any self-signed certificate (which
# would defeat the point of the HIPAA #15 check above).
#
# This is intentionally a private, internal-traffic-only certificate — it never faces the
# internet (Redis itself is always internal-only, reached only by fhirbridge-app/worker within
# the same private network/environment) — unlike the ALB's own self-signed cert (main.tf, aws
# environment), which is documented as "swap for a real ACM certificate" specifically because that
# one fronts real external traffic.
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
