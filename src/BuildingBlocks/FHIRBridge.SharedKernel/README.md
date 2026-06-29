# FHIRBridge.SharedKernel

> The dependency-free heart of the domain model: base entity types, audit/soft-delete contracts, the Result pattern, domain exceptions, and observability abstractions shared by every bounded context.

**Layer:** Building Block / cross-cutting · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
SharedKernel is the lowest-level building block in the solution. It holds the tactical DDD primitives that every other domain and application project builds on, with **zero NuGet or project dependencies** so it can be referenced from anywhere without dragging in infrastructure. It encodes the platform's cross-cutting conventions — aggregate roots with domain events, centrally-stamped audit provenance, soft-delete, optimistic concurrency, an explicit functional `Result`/`Error` type, and PHI-free pipeline-metrics contracts — so those rules are defined once and reused everywhere.

## Responsibilities
- Define the base **entity hierarchy**: `Entity<TId>`, `AggregateRoot<TId>` (with domain-event collection), and the auditable variants used across both the ControlPlane and Runtime domains.
- Define the **audit + soft-delete + concurrency contracts** (`IAuditableEntity`, `ISoftDeletable`, `RowVersion`) that the infrastructure layer's `AuditingSaveChangesInterceptor` stamps automatically, so individual services never set provenance by hand.
- Provide the **`Result` / `Result<T>` / `Error`** functional result types used by application services to model success/failure without throwing.
- Provide the **domain exception base type** (`FHIRBridgeException`) and its concrete members (`BusinessRuleException`, `NotFoundException`).
- Provide **observability abstractions** (`IPipelineMetrics`, `IMetricsSnapshotProvider`, and their PHI-free DTOs) so the application/pipeline layers can record run metrics without depending on OpenTelemetry — the concrete implementation lives in `FHIRBridge.Observability`.

## Key components
- **Abstractions/**
  - `Entity<TId>` — base entity with a protected-set identity.
  - `AggregateRoot<TId>` — entity that accumulates `IDomainEvent`s (`AddDomainEvent`, `DomainEvents`, `ClearDomainEvents`).
  - `IDomainEvent` — marker for domain events, carrying `OccurredOnUtc`.
  - `IAuditableEntity` — creation/modification provenance contract (`CreatedOnUtc/By`, `ModifiedOnUtc/By`, `ApplyCreated`, `ApplyModified`).
  - `ISoftDeletable` — soft-delete contract (`IsDeleted`, `DeletedOnUtc/By`, `ApplyDeleted`); a row is flipped, never physically deleted, so audit/lineage references stay resolvable.
  - `AuditableEntity<TId>` — aggregate root that carries full provenance, soft-delete state and a `byte[] RowVersion` concurrency token.
  - `AuditableChildEntity<TId>` — same audit/soft-delete/concurrency surface for non-aggregate rows (aggregate members, RBAC rows) without the domain-event machinery.
- **Results/**
  - `Error` — `(Code, Message)` record with a shared `Error.None`.
  - `Result` / `Result<T>` — success/failure wrapper; `Value` throws when accessed on a failure.
- **Exceptions/**
  - `FHIRBridgeException` — abstract base for all domain exceptions.
  - `BusinessRuleException` — invariant/business-rule violation.
  - `NotFoundException` — entity-not-found with a formatted message.
- **Observability/**
  - `IPipelineMetrics` + `PipelineRunMetric` — record-a-completed-run contract (counts + timing, PHI-free).
  - `IMetricsSnapshotProvider` + `MetricsSnapshot` + `RecentRunMetric` — in-process aggregated view that drives the admin dashboard.

## Dependencies
- **Projects:** none
- **Key packages:** none (uses only the BCL; `ImplicitUsings` + `Nullable` enabled)
- **Referenced by:** `FHIRBridge.Domain`, `FHIRBridge.Application`, `FHIRBridge.Observability`, `FHIRBridge.ControlPlane.Domain`, `FHIRBridge.ControlPlane.Application`, `FHIRBridge.Runtime.Application` (transitively, the entire solution)

## Current state in this skeleton
Fully ported. All source files present in the reference implementation already exist in this skeleton under `Abstractions/`, `Results/`, `Exceptions/`, and `Observability/` and match the reference. No additional porting is required for this project.

## Roadmap — what it will do in detail
SharedKernel intentionally stays small and stable; growth here ripples through the whole solution, so additions should be genuinely cross-cutting. Expected future work:
- **Strongly-typed identifiers / value objects** — promote raw `Guid`/`string` ids to value objects where invariants justify it, while keeping `Entity<TId>` generic.
- **Domain-event dispatch contract** — a lightweight `IDomainEventDispatcher` abstraction (implemented in infrastructure via MediatR or the EventBus) so aggregates' queued events are published transactionally on save.
- **Richer `Result` ergonomics** — `Map`/`Bind`/`Match`/`Ensure` combinators and implicit conversions to reduce boilerplate in application handlers, plus a multi-error variant for validation aggregation.
- **Specification / paging primitives** — shared `Specification<T>` and `PagedResult<T>` types so query patterns are consistent across ControlPlane and Runtime.
- **Expanded exception taxonomy** — `ConflictException` (optimistic-concurrency / `RowVersion` mismatch), `ValidationException`, and `UnauthorizedDomainException`, all mapped centrally to RFC 7807 problem responses at the API edge.
- **Metrics contract evolution** — additional PHI-free run dimensions (per-source, per-resource-type breakdowns) on `PipelineRunMetric` as the dashboard matures, kept decoupled from any concrete telemetry backend.
