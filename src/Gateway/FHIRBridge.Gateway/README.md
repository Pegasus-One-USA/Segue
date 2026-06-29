# FHIRBridge.Gateway

> The edge reverse proxy for FHIRBridge: a YARP-based gateway that fronts the API (and future internal services), terminating client traffic and routing it to backend clusters with unified, structured logging and observability.

**Layer:** Host (Gateway) · **SDK:** Microsoft.NET.Sdk.Web · **Target:** net9.0

## Purpose
`FHIRBridge.Gateway` is the single ingress point that sits in front of the platform's backend services. It uses [YARP](https://microsoft.github.io/reverse-proxy/) (Yarp.ReverseProxy) to reverse-proxy incoming HTTP requests to configured destination clusters — primarily `FHIRBridge.Api`, and any additional internal services as the platform grows. It centralizes concerns that should live at the edge rather than in each service: routing, TLS termination, cross-cutting headers, and a single observable choke point for all inbound traffic.

## Responsibilities
- Host a Kestrel web server that accepts all external client traffic.
- Reverse-proxy requests to backend clusters via YARP routes/clusters loaded from configuration (`appsettings` `ReverseProxy` section).
- Provide a stable public surface decoupled from the internal topology, so backend services can move or scale without changing client-facing URLs.
- Emit structured, correlated request logging via Serilog and surface gateway telemetry through the shared `FHIRBridge.Observability` building block.
- Serve as the natural future home for edge concerns: TLS, CORS at the edge, rate limiting, header forwarding/propagation, and authentication pre-checks.

## Key components
- **Program.cs (composition root)** — builds the `WebApplication`, will register YARP via `AddReverseProxy().LoadFromConfig(...)`, wire Serilog, register observability, and map the proxy pipeline (`MapReverseProxy()`).
- **YARP reverse proxy (Yarp.ReverseProxy)** — config-driven routing: `routes` match inbound paths/hosts and forward to named `clusters` of destination addresses (e.g. the API service).
- **Serilog (Serilog.AspNetCore)** — structured request and application logging at the edge, enabling end-to-end correlation across the gateway and downstream services.
- **Observability integration (`FHIRBridge.Observability`)** — shared logging/metrics/tracing setup so gateway traffic appears in the same telemetry pipeline as the rest of the platform.

## Dependencies
- **Projects:** `FHIRBridge.Observability` (BuildingBlocks) — shared observability/telemetry wiring.
- **Key packages:**
  - `Yarp.ReverseProxy` — the reverse-proxy engine (routes, clusters, transforms, load balancing, health checks).
  - `Serilog.AspNetCore` — structured request/application logging.

## Current state in this skeleton
Only a placeholder `Program.cs` exists — a minimal `WebApplication` that builds and runs but registers no reverse-proxy routes or middleware. (The reference implementation's `Program.cs` is likewise minimal; the gateway's full routing/observability wiring is intended to be authored from configuration as the proxy topology is finalized.) The YARP and Serilog package references and the `FHIRBridge.Observability` project reference in `FHIRBridge.Gateway.csproj` should match the reference.

## Roadmap — what it will do in detail
1. **Reverse proxy wiring.** In `Program.cs`, call `builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))` and `app.MapReverseProxy()`. Define routes/clusters in `appsettings.json` pointing at the API host (and any additional services).
2. **Logging & observability.** Initialize Serilog (`UseSerilog`) for structured request logging, and register the shared `FHIRBridge.Observability` setup so gateway requests participate in the same metrics/tracing pipeline as downstream services.
3. **Edge cross-cutting concerns.** Add as needed: TLS termination, edge CORS, request/response header transforms (correlation-id propagation, forwarded headers), rate limiting, and active/passive health checks against backend clusters.
4. **Security at the edge (future).** Optional bearer-token pre-validation or pass-through, IP allow-lists, and request-size limits before traffic reaches the API.
5. **Operational hardening (future).** Cluster-level load balancing policies, retry/timeout transforms, and gateway health/readiness endpoints for orchestrator probes.
