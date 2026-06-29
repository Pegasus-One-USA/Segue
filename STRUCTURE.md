# FHIRBridge — Solution Structure

Scaffold copied from `StepBase\FHIRBridge` (.NET 9, Clean Architecture / DDD).
This repo currently contains the **project skeleton** (all `.csproj` with their
project + NuGet references preserved verbatim) plus a **minimum set of base/foundation
classes**. Concrete entities, DTOs, controllers, services, EF configurations and
migrations are intentionally **not** copied — add them per layer as needed.

> The skeleton is structural; it will not fully compile until the concrete types the
> copied base files reference (entities, EF configurations, DI registrations) are added.

## Projects & usage

`.sln` references 16 projects. Five `.csproj` exist on disk but are **not** in the
solution: `FHIRBridge.Contracts`, `FHIRBridge.EventBus`, `FHIRBridge.Messaging`,
`FHIRBridge.ControlPlane.Domain`, `FHIRBridge.ControlPlane.Application`
(add with `dotnet sln add <path>` if wanted).

### BuildingBlocks (cross-cutting, reusable)
| Project | Usage | References | Key NuGet |
|---|---|---|---|
| **FHIRBridge.SharedKernel** | Base abstractions (`Entity`, `AggregateRoot`, `AuditableEntity`, `ISoftDeletable`), `Result`/`Error`, exceptions, metrics interfaces | — | — |
| **FHIRBridge.Observability** | OpenTelemetry + Serilog logging/metrics wiring | SharedKernel | OpenTelemetry.*, Serilog.* , Azure.Monitor |
| **FHIRBridge.Integration** | SQL Server connectivity, HL7 v2 parsing, FHIR bundle building | Runtime.Domain | Microsoft.Data.SqlClient |
| **FHIRBridge.Contracts** *(not in sln)* | Shared integration message contracts | — | — |
| **FHIRBridge.EventBus** *(not in sln)* | MassTransit event-bus abstractions | — | MassTransit (+Azure ServiceBus) |
| **FHIRBridge.Messaging** *(not in sln)* | Azure Service Bus messaging | Contracts | Azure.Messaging.ServiceBus |

### Core (configuration / control side)
| Project | Usage | References | Key NuGet |
|---|---|---|---|
| **FHIRBridge.Domain** | Domain entities/aggregates (Tenant, MappingProfile, SourceConnection, User, Role…), enums, value objects, FHIR resource types | SharedKernel | — |
| **FHIRBridge.Application** | Use cases, DTOs, service interfaces, mapping engine, security/governance abstractions | Domain, Runtime.Application, SharedKernel | MediatR, FluentValidation, YamlDotNet |
| **FHIRBridge.Infrastructure** | EF Core persistence (DbContext, configs, migrations, repositories), destination writers, audit, terminology, messaging impls, KeyVault | Application, Domain, Runtime.Application/Domain/Infrastructure, Integration | EF Core (+SqlServer), Azure.Identity/KeyVault, Npgsql, MySqlConnector, Parquet.Net, QuestPDF, RabbitMQ.Client, SSH.NET |

### Runtime (pipeline execution side)
| Project | Usage | References | Key NuGet |
|---|---|---|---|
| **FHIRBridge.Runtime.Domain** | Pipeline run entities, workflow definitions/nodes/edges, runtime enums, resource envelopes | — | — |
| **FHIRBridge.Runtime.Application** | Pipeline orchestration, CQRS commands/queries, MVC behaviors, workflow catalog/validation, connector/destination abstractions | Runtime.Domain, SharedKernel | MediatR, AutoMapper, FluentValidation |
| **FHIRBridge.Runtime.Infrastructure** | FHIR source clients (Epic/Healow/Meditech), OAuth token providers, destination writers, workflow node executors, run store | Application, Domain, Runtime.Application/Domain, Integration | Hl7.Fhir.R4, Azure.Identity/KeyVault |

### ControlPlane (empty scaffolds — not in sln)
| Project | Usage | References | Key NuGet |
|---|---|---|---|
| **FHIRBridge.ControlPlane.Domain** | (new) control-plane domain | SharedKernel | — |
| **FHIRBridge.ControlPlane.Application** | (new) control-plane use cases | SharedKernel, ControlPlane.Domain | MediatR, AutoMapper, FluentValidation |

### Hosts
| Project | SDK | Usage | References | Key NuGet |
|---|---|---|---|---|
| **FHIRBridge.Api** | Web | REST API, V1 controllers, JWT/Entra auth, Swagger, workflow endpoints | Application, Infrastructure, Domain, Runtime.Application/Infrastructure, Observability | Asp.Versioning, JwtBearer, Microsoft.Identity.Web, Swashbuckle, Serilog.AspNetCore |
| **FHIRBridge.Gateway** | Web | YARP reverse proxy | Observability | Yarp.ReverseProxy, Serilog.AspNetCore |
| **FHIRBridge.Worker** | Worker | Background services: HL7 MLLP listener, pipeline/webhook processors, schedule dispatcher, retention purge | Application, Infrastructure, Runtime.Application/Infrastructure, EventBus, Messaging, Observability | Microsoft.Extensions.Hosting(.WindowsServices), Serilog |

### Tests
| Project | Usage | Key NuGet |
|---|---|---|
| **FHIRBridge.UnitTests** | Domain/Application/Infrastructure/Observability | xUnit, FluentAssertions, EF InMemory |
| **FHIRBridge.Runtime.UnitTests** | Runtime.* | xUnit, Moq, AutoFixture |
| **FHIRBridge.ArchitectureTests** | NetArchTest layering rules | xUnit, NetArchTest.Rules, FluentAssertions |
| **FHIRBridge.LoadTests** | NBomber load tests (Exe, excluded from `dotnet test`) | NBomber, NBomber.Http |

## Base/foundation files present

The solution **builds clean** (`dotnet build` → 0 warnings, 0 errors). Only
dependency-free base files were kept:

- **SharedKernel** — `Entity`, `AggregateRoot`, `AuditableEntity`, `AuditableChildEntity`, `IAuditableEntity`, `IDomainEvent`, `ISoftDeletable`; `Result`, `Error`; `FHIRBridgeException`, `BusinessRuleException`, `NotFoundException`; `IMetricsSnapshotProvider`, `IPipelineMetrics`
- **Runtime.Application** — MediatR pipeline behaviors (`LoggingBehavior`, `PerformanceBehavior`, `ValidationBehavior`) + `Exceptions/ValidationException`
- **Application** — `Mapping/Catalog/fhir-r4-catalog.json` (3-byte stub asset referenced by the csproj)
- **Placeholder entry points** — minimal `Program.cs` in `Api`, `Gateway`, `Worker`, and `LoadTests` (replace with the real bootstrap when porting each host)

## Intentionally trimmed (re-add with their dependencies later)

These base files were copied first but **removed** because they reference concrete
types not yet in the skeleton (Domain entities, DTOs, `ICurrentUserService`, EF
configurations, the Runtime workflow model), which would not compile:

- Audit — `IOperationalAuditService`, `IUserActivityAuditService`
- Persistence — `FHIRBridgeDbContext`, `FHIRBridgeDbContextDesignTimeFactory`, `AuditingSaveChangesInterceptor`
- Abstract base classes — `RelationalDestinationWriterBase`, `WorkflowNodeExecutorBase`

Bring them back once the domain/DTO/abstraction types they depend on are added.
