# FHIRBridge.Runtime.Application

> The application layer of the pipeline-execution side: port abstractions, MediatR CQRS for pipeline runs and bulk export, the pipeline orchestrator + DAG, and the ranked-workflow engine (catalog, validation, executor registry, orchestrator, audit, storage).

**Layer:** Runtime Application · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project orchestrates pipeline execution without binding to any concrete EHR, database, or HTTP technology. It owns the *ports* (interfaces) that Infrastructure implements, the MediatR use cases that start and query runs, the `PipelineOrchestrator` that walks a pipeline DAG from extraction to output, and the **ranked workflow engine** that executes make.com-style node graphs with contract checking, lineage, and audit. It depends only on `FHIRBridge.Runtime.Domain` and the shared kernel, so the same logic runs unchanged behind the API and the Worker.

## Responsibilities
- Define the port abstractions Infrastructure plugs into: auth token providers/cache/audit/code-store, FHIR source/bulk-export/subscription connectors and their factory, destination writers and their factory, the pipeline-run store, the orchestrator, and resource transformers.
- Provide cross-cutting MediatR pipeline behaviors (logging, performance timing, FluentValidation) registered for every request.
- Expose CQRS commands/queries to start a search-based pipeline run, start a bulk-export run, and read back runs/events.
- Implement the `PipelineOrchestrator`: build the stage DAG, fan out extraction concurrently, run governance/transform deterministically, and fan in to the destination — emitting run state and PHI-free events throughout.
- Define the pipeline DAG primitives (`PipelineGraph`, `PipelineStageDefinition`, topological ordering, `ParallelFanOut`) and the default Extraction→Governance→Transform→Output graph.
- Implement the ranked workflow engine: a node catalog with contracts, a graph validator, an executor registry, a topological/rank-ordered orchestrator, audit recording, definition storage, and the typed data-contract payloads passed between nodes.
- Map domain aggregates to DTOs for transport (`PipelineDtoMapper`).

## Key components
### `Abstractions/`
- `Auth/` — `IFhirAccessTokenProvider`, `IFhirAccessTokenCache` (distributed token cache), `IBackendServicesJwtFactory` (+ `BackendServicesJwtRequest`), `IFhirAuthorizationCodeTokenStore` (interactive auth-code tokens), `IFhirAccessTokenAuditSink`.
- `Connectors/` — `IFhirSourceClient` (paginated search), `IFhirBulkExportClient` (`$export` ping-pong), `IFhirSubscriptionClient` (rest-hook Subscription lifecycle), `IFhirSourceClientFactory` (by `RuntimeSourceType`).
- `Destinations/` — `IDestinationWriter`, `IDestinationWriterFactory` (by `RuntimeDestinationType`).
- `Persistence/` — `IPipelineRunStore` (add/update/get/recent runs + add/get events).
- `Pipeline/` — `IPipelineOrchestrator` (`StartAsync`, `StartBulkExportAsync`).
- `Transformations/` — `IResourceTransformer`.

### `Behaviors/`
- `ValidationBehavior.cs`, `LoggingBehavior.cs`, `PerformanceBehavior.cs` — MediatR `IPipelineBehavior`s applied to all requests.

### `Pipeline/`
- `PipelineGraph.cs` — a validated DAG of `PipelineStageDefinition`s; rejects duplicate/dangling stages and cycles and computes a stable topological `ExecutionOrder`.
- `PipelineGraphFactory.cs` — builds the canonical Extraction→Governance→Transform→Output graph.
- `ParallelFanOut.cs` — bounded fan-out/fan-in helper preserving input order; used to extract resource types concurrently.
- `Commands/` — `StartPipelineRunCommand(+Handler,+Validator)`, `StartBulkExportRunCommand(+Handler)`.
- `Queries/` — `GetPipelineRunQuery`, `GetRecentPipelineRunsQuery`, `GetPipelineRunEventsQuery` (+ handlers).

### `Services/`
- `PipelineOrchestrator.cs` — the engine: normalizes requested types, creates the `PipelineRun`, fans out extraction (bounded, max 4), records bookkeeping + governance (PHI-free `ResourceAccessed` events) + transform on a single fan-in thread in DAG order, writes output, completes/fails the run, and persists via `IPipelineRunStore`.

### `Transformations/`
- `FhirResourceNormalizer.cs` — `IResourceTransformer` that re-serializes each resource's JSON to a compact canonical form.

### `DTOs/` & `Mappings/`
- Request/response records: `StartPipelineRunRequest`, `StartBulkExportRunRequest`, `FhirSourceConfiguration`, `RuntimeDestinationConfiguration`, `FhirBulkExportModels` (`BulkExportScope`, `FhirBulkExportRequest`, `BulkExportFile`), `FhirSubscriptionModels`, `StoredOAuthToken`, `PipelineRunDto`/`PipelineStepDto`/`PipelineRunEventDto`. `PipelineDtoMapper` projects aggregates to DTOs.

### `Workflows/`
- `RankedWorkflowOrchestrator.cs` (+ `IRankedWorkflowOrchestrator`) — validates the graph, topologically sorts enabled nodes (ordered by rank → sub-rank → type), executes each via its registered executor, threads each node's output to its downstream edges, records audit events, and emits per-node lineage JSON.
- `WorkflowGraphValidator.cs` (+ `IWorkflowGraphValidator`, `WorkflowGraphValidationResult`, `WorkflowGraphValidationException`) — enforces catalog membership, category/rank agreement, required config fields, rank-ordered edges, data-contract compatibility, mandatory upstream source/mapping paths for destinations/analytics, the "no de-identification after a destination" rule, and acyclicity.
- `WorkflowNodeExecutorRegistry.cs` (+ `IWorkflowNodeExecutor`, `IWorkflowNodeExecutorRegistry`) — resolves an executor by node type.
- `WorkflowExecutionContext.cs`, `WorkflowNodeInput.cs`, `WorkflowNodeOutput.cs`, `WorkflowRunResult.cs`, `WorkflowDataContract.cs` — the execution context, node I/O envelopes, run result, and the typed data-contract enum (`None / ResourceBatch / NormalizedResourceBatch / MappedRecordBatch / DeIdentifiedBatch / DestinationWriteResult / AuditResult`).
- `WorkflowServiceCollectionExtensions.cs` — `AddWorkflowCore()` registers catalog, validator, registry, orchestrator, in-memory definition store, and audit recorder.
- `Catalog/` — `WorkflowNodeTypes` (50+ node-type string constants), `WorkflowNodeCatalogItem`, `IWorkflowNodeCatalog` + `DefaultWorkflowNodeCatalog` (the full catalog: 9 sources, transform/compliance/terminology nodes, 20+ destinations, audit-lineage, analytics — each with rank + input/output contracts).
- `Audit/` — `IWorkflowAuditRecorder`, `WorkflowAuditEvent`, `WorkflowAuditEventType`, `InMemoryWorkflowAuditRecorder`.
- `Storage/` — `IWorkflowDefinitionStore` + `InMemoryWorkflowDefinitionStore`.
- `Payloads/` — typed batch records keyed to the data contracts: `ResourceBatch`, `NormalizedResourceBatch`, `MappedRecordBatch`, `DeIdentifiedBatch`, `DestinationWriteResult`, `AuditResult`.

### Composition
- `DependencyInjection.cs` — `AddRuntimeApplication()` registers MediatR + validators from this assembly, the three behaviors, `FhirResourceNormalizer`, and `PipelineOrchestrator`.

## Dependencies
- **Projects:** `FHIRBridge.Runtime.Domain`, `FHIRBridge.SharedKernel` (BuildingBlocks).
- **Key packages:** MediatR 14.1.0, FluentValidation 12.1.1 (+ DI extensions), AutoMapper 16.1.1, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`.
- **Referenced by:** `FHIRBridge.Runtime.Infrastructure` (and the runtime hosts — API/Worker — that compose them).

## Current state in this skeleton
Partially ported. Present today:
- `Behaviors/` — `LoggingBehavior`, `PerformanceBehavior`, `ValidationBehavior`.
- `Exceptions/ValidationException.cs`.

Still to be ported: the entire `Abstractions/` set, all `DTOs/` and `Mappings/`, the `Pipeline/` graph + commands/queries, the `Services/PipelineOrchestrator`, `Transformations/FhirResourceNormalizer`, the whole `Workflows/` engine (catalog, validator, registry, orchestrator, audit, storage, payloads), and `DependencyInjection.AddRuntimeApplication()`. The `.csproj` already references Domain + SharedKernel and lists all the packages above, so the project is wired to receive them.

## Roadmap — what it will do in detail
- **Two execution models, one orchestrator.** The classic `PipelineOrchestrator` runs a fixed-shape DAG (Extraction→Governance→Transform→Output) and is ideal for "pull N resource types from a source into a destination" jobs and bulk-export ingestion. The `RankedWorkflowOrchestrator` runs arbitrary user-authored graphs from the visual designer. Both will share the connector/destination/auth ports so a node executor and a pipeline stage hit the same Infrastructure.
- **Concurrency where it's safe.** Extraction (read-only source I/O) fans out via `ParallelFanOut` with a bounded degree of parallelism so slow EHR round-trips overlap; the stateful stages (run-state mutation, governance auditing, transform) fan back in to a single thread for deterministic, thread-safe bookkeeping. The DAG makes the ordering explicit and rejects cycles/dangling deps at construction.
- **Contract-checked workflows.** Every catalog node declares input/output `WorkflowDataContract`s; the validator refuses to save a graph whose edges violate contract compatibility, rank ordering, required configuration, or compliance rules (e.g. de-identification must not follow a destination; analytics/destination nodes need an upstream source/mapping path). The orchestrator then executes only validated graphs, threading each node's typed payload to its successors and recording lineage JSON per node for full traceability.
- **Pluggable everything.** Sources, destinations, token grants, the run store, and the workflow definition store are all behind interfaces with factory-based selection, so new EHRs, output formats, and persistence backends are added in Infrastructure without touching this layer. The in-memory stores here are the default for tests and local dev; production swaps in EF/SQL and Redis-backed implementations.
- **Observability and validation as cross-cutting concerns.** The MediatR behaviors give every command/query structured logging, latency measurement, and FluentValidation enforcement for free, keeping handlers thin and the orchestration logic focused.
