# FHIRBridge.Messaging

> The low-level Azure Service Bus client wrapper for sending and receiving the message contracts defined in FHIRBridge.Contracts.

**Layer:** Building Block / cross-cutting · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
Messaging is intended to provide **direct, lower-level access to Azure Service Bus** via the official `Azure.Messaging.ServiceBus` SDK — sending messages to queues/topics and receiving/processing them — for scenarios where the convention-driven MassTransit layer (`FHIRBridge.EventBus`) is heavier than needed. It references `FHIRBridge.Contracts` so it serializes/deserializes the shared message DTOs, and the Worker depends on it for its messaging needs.

## Responsibilities
- Wrap the **Azure Service Bus client** (`ServiceBusClient`, senders, processors/receivers) behind FHIRBridge-friendly abstractions and DI registration.
- Handle **serialization/deserialization** of the shared contracts from `FHIRBridge.Contracts` to and from Service Bus messages, including application properties for routing/correlation.
- Provide **send and receive/processing helpers** — enqueue a message, register a message handler, manage completion/abandon/dead-letter at the SDK level.
- Own **connection/configuration binding** for the Service Bus namespace, queues, and topics used in the direct-client path.

## Key components
> The reference implementation currently contains **no `.cs` files** — only the `Azure.Messaging.ServiceBus` package and the `FHIRBridge.Contracts` project reference. Based on its name, dependencies, and the Worker's reference to it, the project is **intended to hold**:
- **Service Bus client wrapper / DI extension** — e.g. `AddFhirBridgeMessaging(services, configuration)` registering a configured `ServiceBusClient` and senders/processors.
- **Sender abstraction** — a small interface to publish `FHIRBridge.Contracts` messages to a queue/topic with correlation metadata.
- **Receiver/processor abstraction** — registration of message handlers with completion, abandon, and dead-letter semantics.
- **Options type** binding the Service Bus connection string and entity (queue/topic/subscription) names.

## Dependencies
- **Projects:** `FHIRBridge.Contracts`
- **Key packages:** `Azure.Messaging.ServiceBus`
- **Referenced by:** `FHIRBridge.Worker`

## Current state in this skeleton
The `.csproj` exists with the `Azure.Messaging.ServiceBus` package reference and the `FHIRBridge.Contracts` project reference, but **no source files** — matching the reference, where the project is a packaged placeholder. The Worker already references it, so the client wrapper, sender/receiver abstractions, and options binding remain to be authored; the dependency on Contracts means it also waits on Contracts gaining its first message types.

## Roadmap — what it will do in detail
Messaging is the planned escape hatch for direct Service Bus control where MassTransit's conventions are unnecessary or where finer SDK-level control is required:
- **Author a `ServiceBusClient` wrapper + DI extension** that binds the namespace connection string and entity names from configuration and registers reusable senders and processors.
- **Define send/receive abstractions** over `FHIRBridge.Contracts` types so producers/consumers work with strongly-typed messages, not raw `ServiceBusMessage` bodies, with correlation/trace ids in application properties.
- **Provide robust receive semantics** — explicit complete/abandon/dead-letter handling, prefetch, and concurrency tuning at the SDK level.
- **Clarify its boundary with `FHIRBridge.EventBus`** — Messaging for direct, low-ceremony Service Bus interactions; EventBus for MassTransit-based pub/sub, consumers, and sagas — with both serializing the same shared contracts.
- **Integrate observability** — emit trace context and metrics for sends/receives consistent with `FHIRBridge.Observability`, keeping message payloads PHI-free.
