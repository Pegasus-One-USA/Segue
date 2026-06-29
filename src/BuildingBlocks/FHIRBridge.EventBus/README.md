# FHIRBridge.EventBus

> The MassTransit-based event-bus abstraction over Azure Service Bus for publishing and consuming integration events across FHIRBridge services.

**Layer:** Building Block / cross-cutting · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
EventBus is intended to centralize the platform's **publish/subscribe messaging** built on MassTransit with the Azure Service Bus transport. It is the higher-level, convention-driven messaging layer (consumers, sagas, retry/redelivery, topic-per-event routing) that hosts wire up once and reuse — as opposed to the lower-level direct Service Bus client work in `FHIRBridge.Messaging`. The Worker references it to consume integration events and drive pipeline execution asynchronously.

## Responsibilities
- Provide **MassTransit configuration** for FHIRBridge: register the bus, configure the Azure Service Bus transport, and map consumers to endpoints with a consistent naming/topology convention.
- Offer **DI extensions** hosts call to add the event bus and register their consumers.
- Encapsulate cross-cutting messaging concerns — **retry/redelivery policies, error/dead-letter handling, message scheduling, and correlation** — so individual consumers stay focused on business logic.
- Expose a thin **publish/consume abstraction** so application code raises integration events without binding directly to MassTransit or Service Bus APIs.

## Key components
> The reference implementation currently contains **no `.cs` files** — only the MassTransit package references. Based on its name, package set, and the fact the Worker depends on it, the project is **intended to hold**:
- **Bus registration / DI extensions** — e.g. `AddFhirBridgeEventBus(services, configuration)` configuring `AddMassTransit` with `UsingAzureServiceBus`, connection string binding, and endpoint conventions.
- **Consumer base types / registration helpers** — conventions for mapping integration-event consumers (likely consuming the events defined in `FHIRBridge.Contracts`) to Service Bus subscriptions.
- **Resilience configuration** — retry, redelivery, circuit-breaker, and dead-letter policy defaults applied uniformly across consumers.
- **Publish abstraction** — a small interface over `IPublishEndpoint`/`IBus` so producers stay decoupled from MassTransit.

## Dependencies
- **Projects:** none in the reference (expected to reference `FHIRBridge.Contracts` once consumers/events are implemented)
- **Key packages:** `MassTransit`, `MassTransit.Azure.ServiceBus.Core`
- **Referenced by:** `FHIRBridge.Worker`

## Current state in this skeleton
The `.csproj` exists with the MassTransit + Azure Service Bus package references but **no source files** — matching the reference, where the project is a packaged placeholder. The Worker already declares a reference to it, so the bus-configuration and consumer-registration types remain to be authored; until then there is no MassTransit wiring to call.

## Roadmap — what it will do in detail
EventBus is the planned backbone for moving pipeline execution off the synchronous request path and onto a durable, scalable, bus-driven model:
- **Stand up the MassTransit/Azure Service Bus configuration** with a single `AddFhirBridgeEventBus` extension that binds the connection string, configures the transport, and applies endpoint/topic conventions.
- **Register integration-event consumers** (in the Worker) for events defined in `FHIRBridge.Contracts`, so a "run requested" event reliably triggers pipeline execution with at-least-once delivery.
- **Apply platform-wide resilience defaults** — exponential retry, scheduled redelivery, dead-letter routing, and poison-message handling — so transient EHR/destination failures don't lose work.
- **Propagate correlation/trace context** through messages so a pipeline run is observable end-to-end (aligning with `FHIRBridge.Observability`).
- **Support sagas / orchestration** if multi-step async workflows emerge (e.g. fan-out extraction across sources, then aggregate).
- Keep the abstraction thin enough that swapping transports (e.g. local RabbitMQ for dev, Service Bus for prod) is a configuration concern, not a code change.
