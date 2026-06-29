# FHIRBridge.LoadTests

> Standalone NBomber performance / load-testing harness that drives a deployed FHIRBridge environment — run manually, not via `dotnet test`.

**Type:** Load tests · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0 (`OutputType=Exe`)

## Purpose
This project measures how FHIRBridge behaves under realistic and stress-level traffic. It is a console **executable** (`OutputType=Exe`) built on **NBomber** and **NBomber.Http** that fires HTTP load at a *running, deployed* FHIRBridge instance (API/Gateway) and reports throughput, latency percentiles, and error rates. It is intentionally marked `IsTestProject=false`, so it is **excluded from `dotnet test`** and from the CI unit-test gate — it is operated deliberately, on demand, against an environment that is actually up.

## Scope — what it will test
- **API / Gateway endpoints under sustained and bursty load**
  - Configuration/control endpoints (tenants, sources, destinations, mapping profiles, routes) for read/write throughput.
  - Runtime ingestion paths (webhook/ingestion endpoints) that trigger pipeline execution.
  - The synchronous patient-scoped FHIR read endpoint (`GET /api/v1/tenants/{tid}/fhirbridge/Patient/{id}`), including fan-out latency under concurrency.
- **Performance characteristics**
  - Latency distribution (p50/p75/p95/p99) and request throughput (RPS) per scenario.
  - Error and timeout rates as concurrency climbs; identification of saturation/knee points.
  - Sustained-load (soak) stability vs. short spike/stress behavior.
  - Back-pressure and resilience behavior of the gateway and worker/runtime under queue buildup.
- **Capacity & regression signals**
  - Baseline numbers per release to catch performance regressions over time.
  - Headroom estimation for sizing/scaling decisions ahead of Azure Marketplace deployment.

## Test stack
- **Frameworks/tools:** NBomber (load-test scenario engine, metrics, HTML/CSV reports), NBomber.Http (HTTP client/step helpers). No xUnit, no coverlet — this is not a unit-test assembly.
- **Target under test:** a **deployed** FHIRBridge environment (API/Gateway base URL supplied at runtime), not in-process code. No `ProjectReference`s — it talks to the system over the wire.

## Current state in this skeleton
Only the `.csproj` is present in this skeleton repo. No scenario code (`Program.cs` / scenario definitions) exists yet — the harness below is to be authored here (or ported from the reference implementation). The project is configured as a net9.0 console app with NBomber packages referenced and `IsTestProject=false`, so once a `Main` entry point and scenarios are added it builds and runs as a normal executable.

## Roadmap — what it will do in detail
Planned shape of the harness:

- **Entry point (`Program.cs`)** — reads the target base URL, auth token, tenant id, and load profile (warmup, duration, injection rate / concurrent copies) from environment variables or command-line args / config, then composes and runs NBomber scenarios via `NBomberRunner`.
- **Scenarios** — one NBomber `Scenario` per critical path (config CRUD, ingestion/webhook, patient read fan-out), each using NBomber.Http steps to issue requests, with `LoadSimulation`s for ramp-up, constant RPS, spike, and soak. Realistic payloads and per-tenant scoping are built into each step.
- **Reporting** — NBomber's built-in HTML/CSV/markdown reports captured as run artifacts; key metrics (RPS, p95/p99, error %) surfaced for comparison across runs and releases.
- **How to run (manual):**
  ```
  dotnet run --project tests/FHIRBridge.LoadTests -c Release
  ```
  with the target environment, credentials, and load profile supplied via env vars / args. It is deliberately *not* part of `dotnet test`.
- **CI/CD fit:** Not in the PR unit-test gate. Intended for an opt-in, scheduled, or pre-release pipeline stage that points at a dedicated load/staging environment, archives the NBomber reports, and optionally fails the stage on threshold breaches (e.g. p99 or error-rate budgets) to guard against performance regressions before production / Marketplace releases.
