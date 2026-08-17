#!/usr/bin/env bash
# Builds the 3 custom images, brings up the containerized product stack (containerization/compose),
# and checks every public site responds — a one-command "build it and prove it works" loop meant to
# be rerun after every build.
#
# Usage:
#   ./smoke-test.sh                       # build (local tag) + up + check + leave stack running
#   ./smoke-test.sh -t v1.0.1             # build/run a specific tag
#   ./smoke-test.sh -s                    # reuse already-built images, just (re)start + check
#   ./smoke-test.sh -d                    # tear the stack down again after checks pass/fail
#   ./smoke-test.sh -w 300                # allow longer for slow first-boot / DB migration
set -euo pipefail

TAG="local"
SKIP_BUILD="false"
TEAR_DOWN="false"
TIMEOUT=180

while getopts "t:sdw:" opt; do
  case "$opt" in
    t) TAG="$OPTARG" ;;
    s) SKIP_BUILD="true" ;;
    d) TEAR_DOWN="true" ;;
    w) TIMEOUT="$OPTARG" ;;
    *) echo "Usage: $0 [-t tag] [-s] [-d] [-w timeoutSeconds]" >&2; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_DIR="${REPO_ROOT}/containerization/compose"

if [[ ! -f "${COMPOSE_DIR}/.env" ]]; then
  echo "Missing containerization/compose/.env — copy .env.example to .env and fill in secrets first." >&2
  exit 1
fi

if [[ "$SKIP_BUILD" == "false" ]]; then
  echo "==> Building images (tag: ${TAG})"
  "${REPO_ROOT}/containerization/scripts/build-images.sh" -t "${TAG}"
fi

get_env_value() {
  local name="$1" default="$2"
  local val
  val=$(grep -E "^\s*${name}\s*=" "${COMPOSE_DIR}/.env" | tail -n1 | cut -d'=' -f2- | tr -d '[:space:]')
  echo "${val:-$default}"
}
APP_PORT=$(get_env_value "APP_HOST_PORT" "8080")
DEMO_PORT=$(get_env_value "DEMO_HOST_PORT" "5500")

echo "==> Starting stack (docker compose up -d)"
export IMAGE_TAG="${TAG}"
(cd "${COMPOSE_DIR}" && docker compose up -d)

declare -A SITES=(
  ["fhirbridge-app portal"]="http://localhost:${APP_PORT}/"
  ["fhirbridge-app swagger"]="http://localhost:${APP_PORT}/swagger/index.html"
  ["demo-app"]="http://localhost:${DEMO_PORT}/"
)

echo "==> Waiting for sites to respond (timeout: ${TIMEOUT}s each)"
FAILED=0
for name in "${!SITES[@]}"; do
  url="${SITES[$name]}"
  deadline=$((SECONDS + TIMEOUT))
  ok="false"
  while [[ $SECONDS -lt $deadline ]]; do
    if curl -fsS -o /dev/null -m 10 "$url"; then ok="true"; break; fi
    sleep 5
  done
  if [[ "$ok" == "true" ]]; then
    echo "  UP     ${name} (${url})"
  else
    echo "  FAILED ${name} (${url})"
    FAILED=1
  fi
done

if [[ "$TEAR_DOWN" == "true" ]]; then
  echo "==> Tearing stack down (docker compose down)"
  (cd "${COMPOSE_DIR}" && docker compose down)
fi

if [[ "$FAILED" -ne 0 ]]; then
  echo "One or more sites failed to come up." >&2
  exit 1
fi

echo "All sites are up."
