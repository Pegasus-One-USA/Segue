# Builds one image running the Demo app backend (HealthAppBackend), which self-hosts its own
# Angular build the same way FHIRBridge.Api's wwwroot pattern works — same-origin, single process.
# Build from the REPO ROOT as context:
#
#   docker build -f containerization/docker/demo-app/Dockerfile -t demo-app:local .
#
# Optional build arg DEMO_CONFIG selects the Angular build configuration: production (default) |
# staging | qa | dev | working.
# Optional build arg APP_VERSION (set from the image tag by build-images.ps1/.sh) is baked into
# src/app/version.ts so the footer shows which build is actually deployed.

# ---- Stage 1: Angular frontend build ----
FROM node:20-alpine AS frontend-build
ARG DEMO_CONFIG=production
ARG APP_VERSION=local
WORKDIR /src/frontend
COPY Demo_TestApp/frontend/package.json Demo_TestApp/frontend/package-lock.json ./
RUN npm ci
COPY Demo_TestApp/frontend/ ./
RUN echo "export const APP_VERSION = '${APP_VERSION}';" > src/app/version.ts
RUN npm run build -- --configuration=${DEMO_CONFIG}

# ---- Stage 2: .NET publish ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet-build
WORKDIR /src
COPY Demo_TestApp/backend/ ./backend/
RUN dotnet publish backend/HealthAppBackend.csproj \
      -c Release --no-self-contained -o /app/publish

# ---- Stage 3: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=dotnet-build /app/publish ./
COPY --from=frontend-build /src/frontend/dist/frontend/browser ./portal

ENV ASPNETCORE_URLS=http://+:5500
ENV DEMOAPP_PORTAL_PATH=/app/portal

EXPOSE 5500

ENTRYPOINT ["dotnet", "HealthAppBackend.dll"]
