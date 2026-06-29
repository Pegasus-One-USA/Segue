# FHIRBridge.Runtime.UnitTests

> Isolated unit tests for the FHIRBridge pipeline-execution (runtime) side — orchestration, the workflow engine, and connectors, exercised with mocks.

**Type:** Unit tests · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project tests the **runtime** stack that actually executes data-integration pipelines: `FHIRBridge.Runtime.Domain` (execution domain model — pipeline runs, steps, status), `FHIRBridge.Runtime.Application` (orchestration and the workflow engine), and `FHIRBridge.Runtime.Infrastructure` (connectors, transport, and outbound adapters). Unlike the control-side suite, these tests are deliberately **pure unit tests**: external dependencies (source/destination connectors, message bus, HTTP clients, persistence) are replaced with **Moq** doubles, and test data is generated with **AutoFixture**, so each unit of orchestration logic is verified in complete isolation.

## Scope — what it will test
- **Runtime domain (`FHIRBridge.Runtime.Domain`)**
  - `PipelineRun` / step aggregates: lifecycle and status transitions (queued → running → succeeded/failed/cancelled), retry counters, and terminal-state guards.
  - Domain events emitted during execution and the invariants protecting run consistency.
  - Value objects describing execution context (correlation, tenant, source/destination references).
- **Runtime application — orchestration & workflow engine (`FHIRBridge.Runtime.Application`)**
  - The pipeline orchestrator: stage sequencing (extract → map/transform → load), short-circuiting on failure, and best-effort fan-out where applicable.
  - The workflow engine's step dispatch, error handling, retry/back-off decisions, and cancellation propagation — with collaborators mocked.
  - Mapping/transform invocation against configured `MappingProfile`s, pass-through paths (e.g. synchronous patient-scoped reads), and result aggregation into FHIR bundles.
  - Idempotency, deduplication, and correlation handling at the orchestration boundary.
- **Connectors & infrastructure (`FHIRBridge.Runtime.Infrastructure`)**
  - Connector adapters (FHIR/HL7 source readers, destination writers) tested through their interfaces with mocked transports/HTTP clients.
  - Webhook/ingestion entry points and the contracts they hand to the orchestrator.
  - Serialization/translation helpers and resilience wrappers (retry/timeout policy decisions) verified without real network calls.

## Test stack
- **Frameworks/tools:** xUnit (test framework + runner), Moq (mocking collaborators/ports), AutoFixture + AutoFixture.AutoMoq (auto-generated test data and auto-mocked dependencies), FluentAssertions (assertions), coverlet.collector (coverage).
- **Projects under test:**
  - `src/Runtime/FHIRBridge.Runtime.Application`
  - `src/Runtime/FHIRBridge.Runtime.Domain`
  - `src/Runtime/FHIRBridge.Runtime.Infrastructure`

## Current state in this skeleton
Only the `.csproj` is present in this skeleton repo. No test classes exist yet — the suites below are to be authored here (or ported from the reference implementation). The project targets `net9.0` with implicit usings + nullable enabled and a global `Xunit` import, so new mock-based test files run under `dotnet test` as soon as they are added.

## Roadmap — what it will do in detail
Planned structure mirrors the runtime layers:

- **`Domain/`** — fast, dependency-free tests of run/step state machines and invariants. `[Theory]` matrices cover every legal and illegal status transition.
- **`Orchestration/`** — the heart of the suite. Each orchestrator and workflow-engine path gets a fixture that wires an `IFixture` with `AutoMoq`, configures mocked connectors/ports via Moq, drives a scenario (success, partial failure, full failure, cancellation, retry exhaustion), and asserts on the emitted run state, the calls made to collaborators (`Verify`), and the produced output.
- **`Connectors/`** — adapter tests that confirm each connector translates between the engine's contracts and the underlying protocol/client, including error mapping and resilience behavior, using mocked `HttpMessageHandler`/transport doubles.
- **Conventions:** Arrange (AutoFixture builds inputs, Moq sets up ports) — Act — Assert (FluentAssertions on results + `mock.Verify` on interactions). Customizations and `ISpecimenBuilder`s centralize creation of complex runtime objects; one orchestration concern per test.
- **CI fit:** Runs in the standard `dotnet test` pass on every PR/merge alongside `FHIRBridge.UnitTests`. Because everything is mocked, the suite is hermetic and fast — no Docker, broker, database, or external EHR required. It guards the execution semantics that the control-plane configuration ultimately feeds, complementing the config-side coverage and the architecture-rule enforcement.
