# Builds the FHIRBridge.Worker image — background scheduler/pipeline processor, no HTTP endpoint,
# so the runtime stage uses the plain .NET runtime image (no ASP.NET Core needed). Build from the
# REPO ROOT as context:
#
#   docker build -f containerization/docker/worker/Dockerfile -t fhirbridge-worker:local .

# ---- Stage 1: .NET publish ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet-build
WORKDIR /src
COPY src/ ./src/
RUN dotnet publish src/Worker/FHIRBridge.Worker/FHIRBridge.Worker.csproj \
      -c Release --no-self-contained -o /app/publish

# ---- Stage 2: runtime ----
# Despite having no Kestrel endpoint, FHIRBridge.Worker pulls in the ASP.NET Core shared framework
# transitively (via FHIRBridge.Infrastructure/Observability) — the plain dotnet/runtime image is
# missing it and fails at launch with "Framework: 'Microsoft.AspNetCore.App' ... No frameworks
# were found", so this needs the aspnet base image too.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=dotnet-build /app/publish ./

ENTRYPOINT ["dotnet", "FHIRBridge.Worker.dll"]
