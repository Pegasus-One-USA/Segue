# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Backend (.NET 9)

```bash
dotnet build                                          # build solution
dotnet test                                           # run all tests (unit, integration, architecture)
dotnet test tests/FHIRBridge.UnitTests                # unit tests only
dotnet test tests/FHIRBridge.Runtime.UnitTests        # runtime unit tests
dotnet test tests/FHIRBridge.Api.IntegrationTests     # integration tests (in-memory DB)
dotnet test tests/FHIRBridge.ArchitectureTests        # enforce layering rules + no-switch constraints
dotnet run --project src/Api/FHIRBridge.Api           # API host (http://localhost:5000, Swagger at /swagger)
dotnet run --project src/Worker/FHIRBridge.Worker     # background worker host
dotnet run --project tests/FHIRBridge.LoadTests       # load tests (manual, excluded from dotnet test)
```

To run a single test by name:
```bash
dotnet test tests/FHIRBridge.UnitTests --filter "FullyQualifiedName~<TestName>"
```

For EF Core migrations, set the connection string env var first:
```bash
$env:FHIRBRIDGE_DB = "Server=localhost,1433;Database=FHIRBridge;User Id=sa;Password=..."
dotnet ef migrations add <MigrationName> --project src/FHIRBridge.Infrastructure --startup-project src/Api/FHIRBridge.Api
dotnet ef database update --project src/FHIRBridge.Infrastructure --startup-project src/Api/FHIRBridge.Api
```

### Frontend (Angular 20)

```bash
cd portal
npm install
npm start    # ng serve → http://localhost:4200
npm run build
npm test     # Karma/Jasmine
```

### Local E2E (Docker Compose)

```bash
docker-compose up -d   # SQL Server, HAPI FHIR, RabbitMQ, Redis, MinIO, Seq, OTEL collector, output DBs, SFTP
```

Seq (structured logs): http://localhost:5341  
Adminer (database UI): http://localhost:8080

---

## Architecture

### Solution Layout

```
src/
  BuildingBlocks/      # Shared libraries (SharedKernel, Integration, Observability, EventBus, Messaging, Contracts)
  FHIRBridge.Domain    # Core domain entities/aggregates
  FHIRBridge.Application # Use cases, DTOs, CQRS handlers (MediatR)
  FHIRBridge.Infrastructure # EF Core, destination writers, security, terminology
  Api/FHIRBridge.Api   # ASP.NET Core 9 REST API host
  Worker/FHIRBridge.Worker # Background services (scheduler, pipeline processor, HL7 MLLP)
  Runtime/             # Explicit DAG workflow engine (own Domain/Application/Infrastructure layers)
  Gateway/             # YARP reverse proxy stub (no routes configured)
  ControlPlane/        # Empty scaffold for future config/authoring split
tests/
  FHIRBridge.ArchitectureTests
  FHIRBridge.Api.IntegrationTests
  FHIRBridge.UnitTests
  FHIRBridge.Runtime.UnitTests
  FHIRBridge.LoadTests
portal/                # Angular 20 admin UI
docs/backend/          # Architecture docs (01–04)
```

### Clean Architecture Layering (enforced by NetArchTest)

```
Domain ← Application ← Infrastructure ← Api / Worker / Gateway
Runtime.Domain ← Runtime.Application ← Runtime.Infrastructure
All layers ← BuildingBlocks (SharedKernel, Integration, Observability)
```

Violations of this dependency direction will fail `FHIRBridge.ArchitectureTests`.

### Two Coexisting Pipeline Execution Paths

**1. Configured Pipeline** (`IConfiguredPipelineService`)  
Tenant-config-driven; used by the API trigger endpoints and the Worker's scheduled dispatcher. It runs a 4-step normalization pipeline (extension flattening → US-Core validation → data-quality scoring → patient matching), then governance/de-identification, then fans out to one of 21 destination writers.

**2. Runtime Plane** (`PipelineOrchestrator`)  
Explicit DAG with ranked nodes and bounded parallelism (max 4 concurrent resource extractions). Topology: `Extraction (parallel) → Governance → Transform → Output`. Exposed via `/api/v1/workflows` endpoints. Has its own Domain/Application/Infrastructure layers under `src/Runtime/`.

### Domain Root: `Tenant` Aggregate

The `Tenant` aggregate is the multi-tenancy boundary and owns all configuration sub-trees:
- `SourceConnection` (9 source types: Epic, Healow, MEDITECH Greenfield, generic FHIR, HL7v2, flat file, DB, sample, webhook)
- `DestinationConfiguration` (21 types; secrets stored as Key Vault references)
- `MappingProfile` (JsonPath → relational field mappings; terminology, normalization, array policies)
- `ResourcePipelineRoute` (when/how to run: scheduled cron, webhook, or both)

### Registry/Strategy Over Switch — ENFORCED

Architecture tests ban `switch` (and `if`-chains) on `ApplicationType`. Adding a new EHR vendor or auth flow always means a new class registered in DI, never a switch modification.

**Two auth axes are independent:**
- **Vendor axis** (inheritance): `FhirSourceConnectorBase` subclasses (Epic, Healow, MEDITECH, GenericFhir, Sample)
- **ApplicationType axis** (composition via strategy registry): `BackendServicesApplicationStrategy`, `EhrLaunchApplicationStrategy`, `StandaloneApplicationStrategy`, `PatientApplicationStrategy`

Token caching uses `DistributedFhirAccessTokenCache` (Redis in prod, memory in dev). SMART Backend Services (Epic) uses RS384 `private_key_jwt`; Standalone/EHR Launch use OAuth2 PKCE with code stores also backed by Redis.

### EF Core / Persistence

- Single `FHIRBridgeDbContext` with 18 DbSets in `FHIRBridge.Infrastructure`
- Global query filter for soft-deletes (`ISoftDeletable`)
- Optimistic concurrency via `RowVersion`
- Append-only guard on audit log tables (updates/deletes throw)
- Migrations in `src/FHIRBridge.Infrastructure/Persistence/Migrations/`
- Design-time factory reads `FHIRBRIDGE_DB` env var; defaults to `Server=localhost,1433;Database=FHIRBridge`

### API Authentication & Authorization

- Dual JWT scheme: local HS256 (`Authentication:SigningKey`) + Microsoft Entra ID, selected by a custom `IAuthenticationSchemeSelector` policy
- 60-min access tokens, 30-day refresh tokens
- `UnifiedAdmin` policy = GlobalAdmin or TenantAdmin
- Per-permission policies (`HasPermission:<code>`) are auto-registered at startup via reflection over the 28 `PermissionCode` values
- Password-change-required gate middleware runs on every authenticated request

### Worker Background Services

Three hosted services in `src/Worker/FHIRBridge.Worker`:
- `ScheduleDispatcherWorker` — polls every 30 s, finds due `ResourcePipelineRoute` records, enqueues run commands
- `PipelineRunCommandProcessor` — consumes MassTransit messages (InMemory / RabbitMQ / Azure Service Bus, set via `Messaging:Provider`)
- `Hl7MllpListenerService` — TCP MLLP listener for HL7 v2 feeds; gated by `Hl7MllpOptions`

### Building Blocks Status

| Package | Status |
|---|---|
| `FHIRBridge.SharedKernel` | Fully implemented (Entity, AggregateRoot, Result/Error, domain events, metrics interfaces) |
| `FHIRBridge.Integration` | Fully implemented (FHIR JSON, HL7 v2 parser, MLLP, ADT/ORU/MDM → FHIR mapper) |
| `FHIRBridge.Observability` | Fully implemented (OpenTelemetry traces/metrics, Serilog, PHI-masking log enricher) |
| `FHIRBridge.Contracts` | Stub — package ref only, no code |
| `FHIRBridge.EventBus` | Stub — package ref only, no code |
| `FHIRBridge.Messaging` | Stub — package ref only, no code |

### Key Configuration Sections

| Key | Purpose |
|---|---|
| `FHIRBRIDGE_DB` (env var) | EF Core design-time + runtime connection string |
| `Authentication:SigningKey` | JWT HS256 signing key |
| `LocalAuth:SeedAdmin:*` | Bootstrap admin account (email, password, display name) |
| `Portal:AllowedOrigins` | CORS whitelist (default `http://localhost:4200`) |
| `Messaging:Provider` | `InMemory` / `RabbitMQ` / `AzureServiceBus` |
| `RuntimeWorkerOptions` | Feature gates for Worker background services |
| `Hl7MllpOptions` | MLLP listener bind address and port |

### Observability

- PHI-masking Serilog enricher strips identifiers from all log output — do not log raw FHIR resources or patient fields
- OpenTelemetry exports to OTEL collector in Docker Compose; Azure Monitor in cloud
- Seq available locally at http://localhost:5341 for structured log browsing

### Incomplete / Stub Areas

- `Gateway/` — YARP configured but no routes defined yet
- `ControlPlane/` — empty project scaffolds, not included in solution
- `FHIRBridge.Contracts`, `FHIRBridge.EventBus`, `FHIRBridge.Messaging` — project files exist, no implementations
- Runtime Transform step — currently only compacts JSON; field-level mapping is a future milestone
- Some governance methods are pass-through placeholders

### C# Conventions (.editorconfig)

- File-scoped namespaces
- No primary constructors
- `var` only when type is obvious from the right-hand side
- Async methods suffixed with `Async`
- Nullable reference types enabled project-wide
- CRLF line endings, UTF-8
