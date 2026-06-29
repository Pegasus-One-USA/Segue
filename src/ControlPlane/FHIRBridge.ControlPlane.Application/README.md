# FHIRBridge.ControlPlane.Application

> The application/use-case layer for FHIRBridge's future **Control Plane** bounded context — orchestrating the commands, queries, and validation that author, validate, and publish integration configuration.

**Layer:** ControlPlane Application · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project is intended to hold the application layer for the planned **Control Plane** bounded context. It will orchestrate use cases over the Control Plane domain model — creating and updating tenants, connections, destinations, mapping profiles, and pipeline routes; validating them; and publishing them as desired state for the Runtime plane to execute. It follows the same CQRS-with-MediatR, FluentValidation, and AutoMapper pattern already used by the core `FHIRBridge.Application`. This README describes intent only — no code exists yet.

## Responsibilities
- **Define use cases as commands and queries** — MediatR `IRequest`/`IRequestHandler` types for every Control Plane operation (e.g. `CreateSourceConnection`, `UpdateMappingProfile`, `PublishPipelineRoute`, `ListTenantConfigurations`).
- **Validate input** — FluentValidation validators for each command/query, wired into the MediatR pipeline so invalid configuration is rejected before it reaches the domain.
- **Map between contracts and domain** — AutoMapper profiles translating between API/DTO shapes and Control Plane domain aggregates.
- **Coordinate the publish workflow** — drive configuration through its lifecycle (draft → validated → published) and raise/forward the domain events that signal new desired state to the Runtime plane.
- **Depend only on abstractions** — invoke repository and domain-service interfaces defined in `FHIRBridge.ControlPlane.Domain`; concrete persistence/messaging is left to a future Control Plane infrastructure project.
- **Provide DI registration** — a `DependencyInjection`/`AddControlPlaneApplication` extension registering MediatR handlers, validators, and AutoMapper profiles.

## Key components
None exist yet. The intended structure mirrors the core `FHIRBridge.Application`:
- `Features/` (or per-aggregate folders) — `Commands/` and `Queries/` with request types, handlers, and their `Validator` and DTOs co-located.
- `Mappings/` — AutoMapper `Profile` classes.
- `Behaviors/` — MediatR pipeline behaviors (validation, logging) using `Microsoft.Extensions.Logging.Abstractions`.
- `Abstractions/` / `Contracts/` — application-level interfaces and DTOs.
- `DependencyInjection.cs` — the `IServiceCollection` registration entry point.

## Dependencies
- **Projects:** `FHIRBridge.SharedKernel`, `FHIRBridge.ControlPlane.Domain`
- **Key packages:** AutoMapper, FluentValidation, FluentValidation.DependencyInjectionExtensions, MediatR, Microsoft.Extensions.Logging.Abstractions
- **Referenced by:** currently none; intended to be referenced by a future Control Plane API / host and tests.

## Current state in this skeleton
Empty project. It contains only the `.csproj` (referencing `FHIRBridge.SharedKernel` and `FHIRBridge.ControlPlane.Domain`, plus the AutoMapper/FluentValidation/MediatR/Logging.Abstractions packages) with no `.cs` files, and it is **not yet added to the solution (`.sln`)**. It is a scaffolded placeholder for the future Control Plane application layer.

## Roadmap — what it will do in detail
The platform is conceptually splitting into a **Control Plane** (management/configuration — *what* the platform should do) and a **Runtime Plane** (execution — actually ingesting, transforming, and delivering FHIR/HL7 data). This project is the application boundary of the Control Plane: every configuration change enters through a MediatR command, is validated by FluentValidation, mutates a domain aggregate from `FHIRBridge.ControlPlane.Domain`, and is persisted/published via domain-defined abstractions.

How it relates to the existing layers and the Runtime plane:
- **Mirrors the proven core pattern** — the existing `FHIRBridge.Application` already uses MediatR + FluentValidation + AutoMapper for tenant config, mappings, sources, destinations, routes, RBAC, audit, and governance. The same conventions and behaviors carry over here, so the split is structural rather than a rewrite.
- **Authoring vs. execution** — this layer handles *authoring and publishing* configuration. It does not run pipelines. Once a route or mapping is published, the Runtime plane (its own Application/Domain) picks up the desired state and executes it, emitting pipeline runs and run-level audit back out.
- **Decoupled via events/contracts** — handlers raise domain events (e.g. `RoutePublished`) rather than calling Runtime code directly, keeping the two planes independently deployable and testable.
- **Infrastructure-agnostic** — only `Logging.Abstractions` is referenced for cross-cutting logging; concrete EF Core persistence, messaging, and external connectors belong to a future `FHIRBridge.ControlPlane.Infrastructure` project, invoked through interfaces declared in the Domain.

Typical build order when work begins: define aggregates/events/interfaces in `FHIRBridge.ControlPlane.Domain`; add commands, queries, validators, and AutoMapper profiles here; provide the DI registration; add a Control Plane infrastructure project for persistence and a host/API to expose the use cases; and finally add all of these projects to the solution file.
