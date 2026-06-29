# FHIRBridge.ControlPlane.Domain

> The domain model for FHIRBridge's future **Control Plane** bounded context — the management/configuration plane that defines *what* the platform should do, kept separate from the Runtime plane that executes it.

**Layer:** ControlPlane Domain · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project is intended to hold the pure domain model for a planned **Control Plane** bounded context. In the current monolithic design, all configuration concerns (tenants, source connections, destinations, mapping profiles, pipeline routes, scheduling, RBAC, governance, audit) live inside the core `FHIRBridge.Domain`. The Control Plane is a forward-looking refactor that will carve the *management and configuration* responsibilities out of that core domain into their own bounded context, cleanly separated from the **Runtime** (execution) plane that ingests, transforms, and delivers FHIR/HL7 data. This README describes intent only — no code exists yet.

## Responsibilities
- **Own the configuration domain model** — the aggregates, entities, and value objects that describe the *desired state* of an integration: tenants and their settings, source connections, destinations, mapping profiles, pipeline routes, schedules, and policies.
- **Enforce configuration invariants** — domain rules that must hold before a configuration is considered valid and promotable to the Runtime plane (e.g. a route must reference an enabled source and a compatible mapping; a destination must satisfy its connection contract).
- **Express the management lifecycle** — draft → validated → published/active → retired states for configuration aggregates, plus the domain events emitted as configuration changes (so the Runtime plane can react to new or updated desired state).
- **Define governance and policy primitives** — tenancy boundaries, RBAC role/permission concepts, and policy objects that gate what may be configured.
- **Remain infrastructure-free** — no EF Core, no HTTP, no messaging; only the domain language and behavior, building on the shared kernel primitives.

## Key components
None exist yet. The intended structure mirrors the layering already established in the core `FHIRBridge.Domain`:
- `Entities/` and `Aggregates/` — Control Plane aggregate roots such as `Tenant`, `SourceConnection`, `Destination`, `MappingProfile`, `PipelineRoute`, `Schedule`, `Policy`.
- `ValueObjects/` — immutable types like connection endpoints, credentials references, schedule expressions, route keys.
- `Events/` — domain events (`RoutePublished`, `ConnectionEnabled`, `MappingProfileUpdated`) consumed by the Runtime plane.
- `Enums/` — configuration lifecycle states, connection kinds, transport types.
- `Abstractions/` — repository interfaces and domain service contracts (no implementations).
- Base types (`Entity`, `AggregateRoot`, `ValueObject`, `IDomainEvent`) are expected to come from `FHIRBridge.SharedKernel`.

## Dependencies
- **Projects:** `FHIRBridge.SharedKernel`
- **Key packages:** none
- **Referenced by:** currently none; intended to be referenced by `FHIRBridge.ControlPlane.Application` (and, in future, a Control Plane infrastructure/persistence project).

## Current state in this skeleton
Empty project. It contains only the `.csproj` (referencing `FHIRBridge.SharedKernel`) with no `.cs` files, and it is **not yet added to the solution (`.sln`)**. It is a scaffolded placeholder reserving the namespace and project boundary for the future Control Plane bounded context.

## Roadmap — what it will do in detail
The platform is conceptually splitting into two planes:

- **Control Plane** — the *management* plane. It answers "what integrations exist, how are they configured, who may change them, and what policies apply." Configuration is authored, validated, versioned, and published here.
- **Runtime Plane** — the *execution* plane. It consumes published configuration as desired state and actually runs the pipelines: ingesting source data, applying mapping profiles, and delivering to destinations, while emitting pipeline runs, metrics, and audit.

`FHIRBridge.ControlPlane.Domain` is the heart of the Control Plane: the ubiquitous language and invariants for configuration. Over time, the configuration aggregates currently embedded in `FHIRBridge.Domain` (tenant, source, destination, mapping, route, schedule, governance/RBAC) are intended to migrate here, leaving the core Runtime domain focused on execution concepts (pipeline runs, ingestion events, transformation, delivery, run-level audit).

Key design intentions:
- **Clear plane boundary** — the Domain stays persistence- and transport-agnostic. The Runtime plane never mutates Control Plane aggregates directly; it observes *published* desired state and reacts to domain events.
- **Configuration-as-desired-state** — aggregates carry an explicit lifecycle so the Runtime plane can distinguish drafts from live, published configuration.
- **Multi-tenancy and governance first-class** — tenant boundaries and RBAC/policy live in the domain rather than being enforced only at the API edge.
- **Shared kernel reuse** — common DDD building blocks and cross-cutting primitives are taken from `FHIRBridge.SharedKernel` so both planes speak the same base language.

When development begins, the typical order is: add the aggregates/value objects/events here, expose repository and domain-service interfaces, then implement use cases in `FHIRBridge.ControlPlane.Application`, and finally add this project (and its dependents) to the solution file.
