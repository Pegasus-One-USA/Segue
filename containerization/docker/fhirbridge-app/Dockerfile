# Builds one image that runs FHIRBridge.Api (internal, loopback-only) and FHIRBridge.Gateway
# (public — serves the Angular portal build and reverse-proxies /api/** + /swagger/** to the Api)
# as two processes, mirroring the existing Windows Service deploy topology described in
# deploy/windows/README.md. Build from the REPO ROOT as context:
#
#   docker build -f containerization/docker/fhirbridge-app/Dockerfile -t fhirbridge-app:local .
#
# Optional build arg PORTAL_CONFIG selects the Angular build configuration (angular.json):
# production (default) | staging | qa | dev | working.
# Optional build arg APP_VERSION (set from the image tag by build-images.ps1/.sh) is baked into
# src/app/version.ts so the footer shows which build is actually deployed.

# ---- Stage 1: Angular portal build ----
FROM node:20-alpine AS portal-build
ARG PORTAL_CONFIG=production
ARG APP_VERSION=local
WORKDIR /src/portal
# .npmrc (legacy-peer-deps=true) must be present before `npm ci` — without it npm's default
# strict peer-dependency resolution flags several transitive version pairs (chokidar, ajv, keyv)
# as an out-of-sync lockfile, even though the lockfile itself is fine under the project's own
# npm config.
COPY portal/package.json portal/package-lock.json portal/.npmrc ./
RUN npm ci
COPY portal/ ./
RUN echo "export const APP_VERSION = '${APP_VERSION}';" > src/app/version.ts
RUN npm run build -- --configuration=${PORTAL_CONFIG}

# ---- Stage 2: .NET publish (Api + Gateway) ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet-build
WORKDIR /src
COPY src/ ./src/
RUN dotnet publish src/Api/FHIRBridge.Api/FHIRBridge.Api.csproj \
      -c Release --no-self-contained -o /app/publish/api \
    && dotnet publish src/Gateway/FHIRBridge.Gateway/FHIRBridge.Gateway.csproj \
      -c Release --no-self-contained -o /app/publish/gateway

# ---- Stage 3: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=dotnet-build /app/publish/api ./api
COPY --from=dotnet-build /app/publish/gateway ./gateway
COPY --from=portal-build /src/portal/dist/fhirbridge-ui/browser ./portal
COPY containerization/docker/fhirbridge-app/entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

# The Api never listens outside this container (127.0.0.1 only, set in entrypoint.sh) — only the
# Gateway's port is meant to be published by the orchestrator.
EXPOSE 80

ENTRYPOINT ["./entrypoint.sh"]
