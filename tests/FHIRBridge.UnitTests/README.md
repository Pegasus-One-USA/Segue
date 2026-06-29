# FHIRBridge.UnitTests

> Unit and in-memory integration tests for the core configuration / control side of FHIRBridge — domain rules, application services, and EF Core persistence.

**Type:** Unit/Integration tests · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project verifies the behavior of FHIRBridge's core "control" stack: the domain model (`FHIRBridge.Domain`), the application/use-case layer (`FHIRBridge.Application`), and the EF Core persistence layer (`FHIRBridge.Infrastructure`). It exercises business invariants, application orchestration, validation, and data-access mappings without touching real infrastructure — the EF Core **InMemory** provider stands in for SQL Server so persistence and query logic can be tested deterministically and fast. It is the primary safety net for the configuration plane (tenants, sources, destinations, mapping profiles, pipeline routes) that the runtime later consumes.

## Scope — what it will test
- **Domain layer (`FHIRBridge.Domain`)**
  - Entity invariants and guard clauses (e.g. a `MappingProfile` must have a `ResourceType`, a route must reference a valid mapping).
  - Value objects, enums, and domain events raised on state changes.
  - Aggregate behavior: enable/disable (`IsEnabled`) transitions, soft-delete semantics, `RowVersion`/concurrency markers, and the `AuditableChildEntity` audit fields.
  - Factory methods and domain services that enforce business rules independent of persistence.
- **Application layer (`FHIRBridge.Application`)**
  - Command/query handlers and application services for configuring tenants, sources, destinations, mapping profiles, and resource pipeline routes.
  - Input validation (FluentValidation-style validators) and the resulting error/Result contracts.
  - Mapping between domain entities and API DTOs/contracts; ensuring API-facing shapes are preserved (e.g. derived resource-type grouping from `MappingProfile.ResourceType`).
  - Authorization/tenant-scoping logic where it lives in the application layer.
- **Infrastructure / persistence (`FHIRBridge.Infrastructure`)**
  - EF Core `DbContext` configuration: entity type configurations, relationships, owned types, indexes, and query filters (e.g. soft-delete global filters).
  - Repository implementations and `SaveChanges` behavior, validated against the **InMemory** provider.
  - The `AuditingSaveChangesInterceptor` (audit-stamp population) and hash-chained `UserActivityAuditLog` write path.
  - Configuration binding via `Microsoft.Extensions.Configuration` for options classes consumed by infrastructure services.
- **Observability (`FHIRBridge.Observability`)**
  - Smoke-level checks of telemetry/logging helpers, activity sources, and metric registration that the core stack depends on.

## Test stack
- **Frameworks/tools:** xUnit (test framework + runner), FluentAssertions (expressive assertions), Microsoft.EntityFrameworkCore.InMemory (in-memory persistence provider), Microsoft.Extensions.Configuration (config binding in tests), coverlet.collector (code coverage).
- **Projects under test:**
  - `src/FHIRBridge.Application`
  - `src/FHIRBridge.Domain`
  - `src/FHIRBridge.Infrastructure`
  - `src/BuildingBlocks/FHIRBridge.Observability`

## Current state in this skeleton
Only the `.csproj` is present in this skeleton repo. No test classes have been added yet — the suites described below are to be authored here (or ported from the reference implementation). The project already targets `net9.0`, enables implicit usings + nullable, and globally imports `Xunit`, so new `*.cs` test files can be dropped in and run with `dotnet test` immediately.

## Roadmap — what it will do in detail
Planned structure mirrors the layers it references:

- **`Domain/`** — pure, fast unit tests with no provider. Categories: aggregate construction and invariants, state transitions (enable/disable, soft-delete), domain-event emission, and value-object equality. These run in microseconds and form the bulk of the suite.
- **`Application/`** — handler/service tests using hand-built fakes or lightweight stubs for outbound ports. Categories: happy-path use cases, validation failures, tenant-scoping enforcement, and DTO/contract mapping. Each public application service should have a corresponding test fixture.
- **`Persistence/` (integration-flavored)** — tests that spin up a `DbContext` on the InMemory provider with a fresh uniquely-named database per test, seed entities, and assert on configuration (filters, relationships), interceptor behavior (audit stamps, row versions), and query results. These verify the wiring between domain and EF Core without a database server.
- **Conventions:** Arrange-Act-Assert with FluentAssertions; one logical assertion theme per test; `[Theory]`/`[InlineData]` for boundary and validation matrices; shared fixtures via `IClassFixture<>`/collection fixtures for DbContext setup.
- **CI fit:** Runs in the standard `dotnet test` pass on every PR and merge. coverlet emits coverage (Cobertura) for gating/reporting. Being InMemory-only, it requires no Docker, SQL Server, or network — keeping the inner-loop and CI fast and hermetic. It pairs with `FHIRBridge.Runtime.UnitTests` (execution side) and `FHIRBridge.ArchitectureTests` (layering rules) to cover the full control-plane contract.
