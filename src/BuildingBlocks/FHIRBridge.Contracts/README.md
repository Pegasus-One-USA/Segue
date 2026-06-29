# FHIRBridge.Contracts

> The shared, dependency-free wire contracts (integration events and message DTOs) exchanged between FHIRBridge services over the message bus.

**Layer:** Building Block / cross-cutting · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
Contracts is the neutral, shared-language project for asynchronous communication between bounded contexts. It is intended to hold the **integration-event and command/message DTOs** that producers (API, ControlPlane) and consumers (Worker, Runtime) agree on, so neither side has to reference the other's domain or application assemblies. Keeping these types in a tiny standalone project lets both ends share one definition of "what travels on the bus" while staying decoupled.

## Responsibilities
- Define the **public message schema** for inter-service messaging: integration events (e.g. "pipeline run requested", "ingestion received", "run completed") and any command envelopes published to / consumed from Azure Service Bus.
- Stay **dependency-free** (no domain, application, or infrastructure references) so it can be referenced by any producer or consumer without coupling them together.
- Serve as the **versioned, backward-compatibility-sensitive boundary** between services — changes here are wire-protocol changes.

## Key components
> The reference implementation currently contains **no `.cs` files** — the project exists as a placeholder with no type yet referenced by any other project. Based on its name, its position as the dependency of `FHIRBridge.Messaging`, and the platform's pipeline architecture, it is **intended to hold**:
- **Integration events** — immutable records describing facts other services react to (e.g. `PipelineRunRequested`, `IngestionReceived`, `PipelineRunCompleted`), each carrying `TenantId`, correlation ids, and PHI-free payloads.
- **Command/message envelopes** — request messages enqueued for the Worker to process asynchronously.
- **Shared enums / constants** for message status and routing, plus message-versioning metadata.

## Dependencies
- **Projects:** none
- **Key packages:** none (`ImplicitUsings` + `Nullable` enabled)
- **Referenced by:** `FHIRBridge.Messaging` (which references Contracts directly). Not yet referenced by any other project in the reference implementation.

## Current state in this skeleton
The `.csproj` exists with no source files and no dependencies — identical to the reference, where the project is an empty placeholder. There is nothing to port yet; the project is awaiting its first contract types once the asynchronous messaging path is fleshed out.

## Roadmap — what it will do in detail
Contracts becomes load-bearing as the platform moves pipeline execution onto an asynchronous, bus-driven model:
- **Author the first integration events** for the async pipeline — request/accepted/completed/failed events — as immutable `record` types with stable property names, all PHI-free (ids, counts, status, timing only), mirroring the dashboard's PHI-free metrics discipline.
- **Establish versioning conventions** — additive-only changes, explicit message versions, and namespacing so producers and consumers can evolve independently without breaking in-flight messages.
- **Define message contracts consumed by MassTransit / Azure Service Bus** so `FHIRBridge.EventBus` and `FHIRBridge.Messaging` bind to these shared types rather than ad-hoc payloads.
- **Document the topic/queue topology** these contracts map to, keeping the wire schema and routing in one reviewable place.
- Remain deliberately **thin and dependency-free**, so it can be referenced by every service tier without creating coupling — the whole point of a contracts assembly.
