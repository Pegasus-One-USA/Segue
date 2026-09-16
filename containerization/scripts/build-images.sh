#!/usr/bin/env bash
# Builds (and optionally pushes) the 5 custom Segue container images from the repo root -- so
# one command refreshes everything the Container Apps environment actually deploys.
# Terraform's azure/aws environments assume this has already been run against their registry.
# segue-redis (containerization/docker/redis-tls) is stock redis:7-alpine plus a fixed,
# committed self-signed TLS certificate — see that Dockerfile's own comment for why it's a custom
# image at all (FHIRBridge.Api/.Worker refuse a plaintext Redis connection outside Development).
# segue-postgres (containerization/docker/postgres-local) is stock postgres:16-alpine plus a
# custom entrypoint that keeps PGDATA on local ephemeral disk and treats a mounted Azure Files
# share purely as a backup target — see that Dockerfile's own comment for why (Postgres's startup
# permission check can never pass directly on Azure Files/SMB).
# segue-postgres-backup (containerization/docker/postgres-backup) is stock postgres:16-alpine
# plus azcopy and a pg_dump-to-Blob-Storage script — only actually deployed (as a scheduled
# Container Apps Job) when a client's Bicep/Terraform deployment keeps Postgres containerized
# instead of using the managed Azure Database for PostgreSQL path; see that Dockerfile's own comment.
#
# Usage:
#   ./build-images.sh                                   # local tags only, no push
#   ./build-images.sh -t v1.2.0                          # local tags with a specific version
#   ./build-images.sh -r myregistry.azurecr.io -t v1.2.0 -p   # build+push all 5, ACR
#   ./build-images.sh -r 123456789012.dkr.ecr.us-east-1.amazonaws.com/segue -t v1.2.0 -p  # ECR, all 5
#
# -r REGISTRY   registry/repo prefix images are tagged with (default: none — local tag only)
# -t TAG        image tag (default: local)
# -p            push each image after building (requires -r and that you're already logged in
#               to the registry, e.g. `az acr login` / `aws ecr get-login-password | docker login`)
set -euo pipefail

REGISTRY=""
TAG="local"
PUSH="false"

while getopts "r:t:p" opt; do
  case "$opt" in
    r) REGISTRY="$OPTARG" ;;
    t) TAG="$OPTARG" ;;
    p) PUSH="true" ;;
    *) echo "Usage: $0 [-r registry] [-t tag] [-p]" >&2; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PREFIX=""
if [[ -n "$REGISTRY" ]]; then
  PREFIX="${REGISTRY%/}/"
fi

declare -A IMAGES=(
  [segue-app]="containerization/docker/segue-app/Dockerfile"
  [segue-worker]="containerization/docker/worker/Dockerfile"
  [segue-redis]="containerization/docker/redis-tls/Dockerfile"
  [segue-postgres]="containerization/docker/postgres-local/Dockerfile"
  [segue-postgres-backup]="containerization/docker/postgres-backup/Dockerfile"
)
# segue-app bakes $TAG into its Angular build (footer version display) -
# segue-worker has no UI, so it doesn't take this build-arg.
UI_IMAGES=("segue-app")

for name in "${!IMAGES[@]}"; do
  dockerfile="${IMAGES[$name]}"
  full_tag="${PREFIX}${name}:${TAG}"
  build_args=()
  for ui in "${UI_IMAGES[@]}"; do
    if [[ "$ui" == "$name" ]]; then build_args=(--build-arg "APP_VERSION=${TAG}"); fi
  done
  echo "==> Building ${full_tag} (${dockerfile})"
  docker build -f "${REPO_ROOT}/${dockerfile}" -t "${full_tag}" "${build_args[@]}" "${REPO_ROOT}"

  if [[ "$PUSH" == "true" ]]; then
    if [[ -z "$REGISTRY" ]]; then
      echo "Error: -p requires -r <registry>" >&2
      exit 1
    fi
    echo "==> Pushing ${full_tag}"
    docker push "${full_tag}"
  fi
done

echo "Done. Images tagged with prefix '${PREFIX}' and tag '${TAG}'."
