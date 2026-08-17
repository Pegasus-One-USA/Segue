#!/bin/bash
# Runs FHIRBridge.Api (internal, loopback-only) and FHIRBridge.Gateway (public, serves the portal
# + proxies /api and /swagger to the Api) as two sibling processes in one container — the same
# topology deploy/windows/README.md describes for the Windows Service deployment, just without a
# service manager. Each process gets its own ASPNETCORE_URLS via a scoped `env` invocation rather
# than a container-wide ENV, since the two processes must NOT share the same bind address.
#
# ASPNETCORE_CONTENTROOT is likewise set per-process and is NOT optional: ASP.NET Core resolves
# appsettings.json (and wwwroot) relative to the content root, which defaults to the process's
# current working directory — NOT the directory containing its DLL. Both `dotnet` invocations
# below run from this script's own cwd (/app, the image's WORKDIR), so without an explicit content
# root each process would look for its config next to /app instead of /app/api or /app/gateway and
# silently start with none of it — Gateway in particular would load zero YARP routes.
#
# Gateway's own Program.cs refuses to start in any non-dev environment without ApiBaseUrl set
# explicitly (no silent fallback — a deliberate guard against one misconfigured environment
# proxying into another's Api). Since Api and Gateway are always paired 1:1 in this same container
# at this fixed loopback address, it's set here to exactly what Api binds to two lines below,
# rather than relying on that removed default.
set -e

env ASPNETCORE_URLS=http://127.0.0.1:5000 \
    ASPNETCORE_CONTENTROOT=/app/api \
    dotnet /app/api/FHIRBridge.Api.dll &
API_PID=$!

env ASPNETCORE_URLS=http://+:80 \
    ASPNETCORE_CONTENTROOT=/app/gateway \
    StaticFiles__RootPath=/app/portal \
    ApiBaseUrl=http://127.0.0.1:5000/ \
    dotnet /app/gateway/FHIRBridge.Gateway.dll &
GATEWAY_PID=$!

terminate() {
  kill -TERM "$API_PID" "$GATEWAY_PID" 2>/dev/null || true
}
trap terminate TERM INT

# If either process dies, the container should die too so the orchestrator restarts it.
wait -n "$API_PID" "$GATEWAY_PID"
EXIT_CODE=$?
terminate
exit $EXIT_CODE
