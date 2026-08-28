#!/usr/bin/env bash
# Builds (and optionally pushes) the 4 custom FHIRBridge container images from the repo root, plus
# (Azure/ACR only, when -p is set) imports hapi-terminology under the same tag -- so one command
# refreshes everything the Container Apps environment actually deploys.
# Terraform's azure/aws environments assume this has already been run against their registry.
# fhirbridge-redis (containerization/docker/redis-tls) is stock redis:7-alpine plus a fixed,
# committed self-signed TLS certificate — see that Dockerfile's own comment for why it's a custom
# image at all (FHIRBridge.Api/.Worker refuse a plaintext Redis connection outside Development).
# hapi-terminology is different from the other 4 -- it's a stock third-party image (hapiproject/hapi
# on Docker Hub), not built from a Dockerfile we own, so there's nothing to `docker build` for it.
# Against an ACR (-r ...azurecr.io) it's imported via `az acr import` (a server-to-server copy, no
# local Docker involved). Against any other registry (ECR included), it falls back to a local
# `docker pull` + `tag` + `push` -- slower (goes through local bandwidth/disk) but works anywhere.
#
# Usage:
#   ./build-images.sh                                   # local tags only, no push
#   ./build-images.sh -t v1.2.0                          # local tags with a specific version
#   ./build-images.sh -r myregistry.azurecr.io -t v1.2.0 -p   # build+push+import all 5, ACR (server-to-server)
#   ./build-images.sh -r 123456789012.dkr.ecr.us-east-1.amazonaws.com/fhirbridge -t v1.2.0 -p  # ECR, all 5 (hapi-terminology via local pull/tag/push)
#
# -r REGISTRY   registry/repo prefix images are tagged with (default: none — local tag only)
# -t TAG        image tag (default: local)
# -p            push each image after building (requires -r and that you're already logged in
#               to the registry, e.g. `az acr login` / `aws ecr get-login-password | docker login`)
# -s SOURCE     upstream image to import as hapi-terminology (default: docker.io/hapiproject/hapi:latest
#               at the time this runs -- re-run to pick up a newer upstream release)
# -x            don't import hapi-terminology even when -p + an ACR -r are set
set -euo pipefail

REGISTRY=""
TAG="local"
PUSH="false"
HAPI_TERMINOLOGY_SOURCE="docker.io/hapiproject/hapi:latest"
SKIP_HAPI_TERMINOLOGY="false"

while getopts "r:t:ps:x" opt; do
  case "$opt" in
    r) REGISTRY="$OPTARG" ;;
    t) TAG="$OPTARG" ;;
    p) PUSH="true" ;;
    s) HAPI_TERMINOLOGY_SOURCE="$OPTARG" ;;
    x) SKIP_HAPI_TERMINOLOGY="true" ;;
    *) echo "Usage: $0 [-r registry] [-t tag] [-p] [-s hapi-terminology-source] [-x]" >&2; exit 1 ;;
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
  [fhirbridge-redis]="containerization/docker/redis-tls/Dockerfile"
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

if [[ "$PUSH" == "true" && "$SKIP_HAPI_TERMINOLOGY" != "true" ]]; then
  if [[ "$REGISTRY" == *.azurecr.io ]]; then
    # Server-to-server copy (Azure's backend pulls hapiproject/hapi directly) - no local
    # download/re-upload of this (large) image needed, unlike the generic pull/tag/push fallback
    # below.
    ACR_NAME="${REGISTRY%.azurecr.io}"
    if ! command -v az >/dev/null 2>&1; then
      echo "Warning: skipping hapi-terminology import -- 'az' CLI not found on PATH." >&2
    else
      echo "==> Importing hapi-terminology:${TAG} from ${HAPI_TERMINOLOGY_SOURCE} into ${REGISTRY} (server-to-server, via az acr import)"
      az acr import --name "$ACR_NAME" --source "$HAPI_TERMINOLOGY_SOURCE" --image "hapi-terminology:${TAG}" --force
    fi
  else
    # No ECR (or generic registry) equivalent to az acr import's server-to-server copy exists -
    # this has to go through local Docker: pull the upstream image, retag it, push it. Slower and
    # uses local bandwidth/disk, but works against any registry, not just ACR.
    echo "==> Pulling ${HAPI_TERMINOLOGY_SOURCE} (no server-to-server import available for '${REGISTRY}' - falling back to local pull/tag/push)"
    docker pull "$HAPI_TERMINOLOGY_SOURCE"
    HAPI_FULL_TAG="${PREFIX}hapi-terminology:${TAG}"
    docker tag "$HAPI_TERMINOLOGY_SOURCE" "$HAPI_FULL_TAG"
    echo "==> Pushing ${HAPI_FULL_TAG}"
    docker push "$HAPI_FULL_TAG"
  fi
fi

echo "Done. Images tagged with prefix '${PREFIX}' and tag '${TAG}'."
