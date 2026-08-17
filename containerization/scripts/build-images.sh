#!/usr/bin/env bash
# Builds (and optionally pushes) the 3 custom FHIRBridge container images from the repo root.
# Terraform's azure/aws environments assume this has already been run against their registry.
#
# Usage:
#   ./build-images.sh                                   # local tags only, no push
#   ./build-images.sh -t v1.2.0                          # local tags with a specific version
#   ./build-images.sh -r myregistry.azurecr.io -t v1.2.0 -p   # build, tag, and push to ACR
#   ./build-images.sh -r 123456789012.dkr.ecr.us-east-1.amazonaws.com/fhirbridge -t v1.2.0 -p  # ECR
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
  [fhirbridge-app]="containerization/docker/fhirbridge-app/Dockerfile"
  [demo-app]="containerization/docker/demo-app/Dockerfile"
  [fhirbridge-worker]="containerization/docker/worker/Dockerfile"
)
# fhirbridge-app and demo-app bake $TAG into their Angular build (footer version display) -
# fhirbridge-worker has no UI, so it doesn't take this build-arg.
UI_IMAGES=("fhirbridge-app" "demo-app")

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
