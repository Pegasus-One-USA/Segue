# FHIRBridge.Runtime.Domain

> The pure domain model of the pipeline-execution (runtime) side of FHIRBridge: pipeline-run aggregates, runtime enums, value objects, the supported-FHIR catalog, and the ranked workflow graph model.

**Layer:** Runtime Domain · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project holds the runtime-side domain entities and value objects that describe *what happens when a pipeline runs* — as distinct from the configuration/control plane in the core `FHIRBridge.Domain` (tenants, sources, mapping profiles, routes). It is a dependency-free, behaviour-rich model: aggregates such as `PipelineRun` enforce their own state transitions, and the `Workflows` model expresses a make.com-style ranked node/edge graph with its own validation invariants. Everything here is plain C# with no NuGet dependencies, so it can be referenced by Application and Infrastructure without dragging in frameworks.

## Responsibilities
- Model a single execution of a pipeline as the `PipelineRun` aggregate, owning its `PipelineRunStep` children and tracking extracted/written counts, status, and timing.
- Model PHI-free observability events (`PipelineRunEvent`) emitted as a run progresses.
- Define the runtime enumerations that classify a run: source type, destination type, step type, and run/step status.
- Provide transport-neutral value objects (`ResourceEnvelope`, `ResourceFetchFailure`) for resources flowing through the pipeline and for best-effort fetch failures surfaced as `OperationOutcome`.
- Maintain the canonical list of supported FHIR R4 resource types and the normalization/validation helpers around it.
- Define the **ranked workflow graph** model — `WorkflowDefinition` (nodes + edges), `WorkflowNode`, `WorkflowEdge`, `WorkflowNodeConfiguration`, their categories/ranks, and the `WorkflowRun`/`WorkflowNodeRun` execution records — together with the rank policy that constrains where a node category may sit in the graph.

## Key components
### `Entities/`
- `PipelineRun.cs` — the run aggregate root; constructed for a tenant/source/destination + requested resource types, exposes `StartStep`, `AddExtractedResources`, `AddWrittenResources`, `Complete`, `Fail`, and guards `Status`/counters behind private setters.
- `PipelineRunStep.cs` — a single stage occurrence (e.g. Extraction of `Observation`) with `Complete(count, message)` / `Fail(message)`; constructed only via `PipelineRun.StartStep` (internal ctor).
- `PipelineRunEvent.cs` — an immutable, append-only audit/telemetry event (event type, optional step/resource type/id, message, correlation id, timestamp); deliberately carries no clinical payload.

### `Enums/`
- `PipelineRunStatus.cs` — `Pending / Running / Completed / Failed`.
- `PipelineStepStatus.cs` — per-step lifecycle, same four states.
- `PipelineStepType.cs` — `Extraction / Governance / Transform / Output` (the canonical four-stage flow).
- `RuntimeSourceType.cs` — `Epic / Sample / Cerner / Allscripts / GenericFhir / Healow / MeditechGreenfield`.
- `RuntimeDestinationType.cs` — `SqlServer / InMemory`.

### `ValueObjects/`
- `ResourceEnvelope.cs` — immutable record `(ResourceType, ResourceId?, RawJson, VersionId?, LastUpdated?)`; the unit of data passed between pipeline stages.
- `ResourceFetchFailure.cs` — `(ResourceType, Message)`; records a single failed fetch during best-effort aggregation so it can be rendered as an `OperationOutcome` entry instead of failing the whole request.

### `Fhir/`
- `SupportedFhirResourceTypes.cs` — the allow-list of currently supported R4 types (Patient, Observation, Condition, MedicationRequest, AllergyIntolerance, Encounter, DiagnosticReport, Procedure, Immunization) with case-insensitive `IsSupported` / `Normalize`.

### `Workflows/`
- `WorkflowDefinition.cs` — aggregate owning nodes and edges; factory methods `AddNode`, `AddEdge`, `AddNodeConfiguration`; tenant-scoped, versioned, enable/disable (`Activate`/`Deactivate`).
- `WorkflowNode.cs` — a node with `NodeType`, `Category`, `Rank`/`SubRank`, display name, `ConfigurationJson`, canvas position, and key/value `Configuration` entries.
- `WorkflowEdge.cs` — a directed edge between two distinct nodes, validated on construction.
- `WorkflowNodeConfiguration.cs` — a single key/value setting on a node, with an `IsSecret` flag.
- `WorkflowNodeCategory.cs` — `Source(0) / Transform(10) / Compliance(20) / Destination(30) / Analytics(40)`.
- `WorkflowRankPolicy.cs` — static policy enforcing that Source nodes sit at rank 0 and all other categories sit in `(0,100)`.
- `WorkflowRun.cs` / `WorkflowNodeRun.cs` — execution records: a run owns per-node runs, each carrying status, timing, lineage JSON, and error message; `Succeed`/`Fail` transitions.
- `WorkflowRunStatus.cs` — `Pending / Running / Succeeded / Failed / Cancelled`.

## Dependencies
- **Projects:** none (intentionally — a leaf domain library).
- **Key packages:** none. `ImplicitUsings` and `Nullable` enabled; pure BCL only.
- **Referenced by:** `FHIRBridge.Runtime.Application` and `FHIRBridge.Runtime.Infrastructure`.

## Current state in this skeleton
Empty — the `.csproj` exists with no `.cs` files. None of the entities, enums, value objects, the FHIR catalog, or the Workflows model have been ported yet. All components listed above are still to be created in this skeleton; they exist in full in the reference implementation.

## Roadmap — what it will do in detail
- **Pipeline-run aggregate.** `PipelineRun` becomes the single source of truth for an execution: it is created when a run is requested, accumulates `PipelineRunStep` children as the orchestrator walks the Extraction→Governance→Transform→Output stages, tallies extracted vs. written resource counts, and transitions to `Completed`/`Failed` with a failure message. All mutation goes through intention-revealing methods so the invariants (you cannot complete a failed run, counts only increase) live in the domain.
- **PHI-free event stream.** Each meaningful moment (`PipelineStarted`, `ExtractionCompleted`, `ResourceAccessed`, `TransformCompleted`, `OutputCompleted`, `PipelineFailed`) is captured as a `PipelineRunEvent`. Events reference a resource type/id but never the payload, so the audit trail is safe to retain and surface to operators and compliance.
- **Supported-type catalog.** `SupportedFhirResourceTypes` is the gate the orchestrator uses to normalize/validate requested types; it will expand toward full US Core coverage as connectors mature, and may eventually be backed by the Firely `ModelInfo` metadata rather than a hand-curated list.
- **Ranked workflow graph.** The `Workflows` model is the heart of the make.com-style designer. A `WorkflowDefinition` is a DAG of typed nodes laid out on a canvas; ranks impose a coarse left-to-right ordering (Source at 0, then Compliance/Transform/Destination/Analytics bands) while edges express explicit data flow. `WorkflowRankPolicy` keeps the graph well-formed at the domain level, and `WorkflowRun`/`WorkflowNodeRun` record each execution with per-node lineage JSON for end-to-end traceability. Future work: richer rank bands per category, branch/merge semantics, retry/compensation metadata on node runs, and persistence-friendly value objects for the designer's save/version flow.
- **Stays dependency-free.** As the runtime grows, this project remains the framework-agnostic core: no MediatR, no EF, no HTTP — only the model and its rules, so it can be unit-tested in isolation and reused across the API, Worker, and any future hosts.
