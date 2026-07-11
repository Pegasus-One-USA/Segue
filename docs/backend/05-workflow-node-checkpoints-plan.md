# 05 — Workflow Node Checkpoints ("Copy URL" per node) — Implementation Plan

> Part of the [Backend Architecture Guide](README.md).
> **Status: PROPOSED / NOT YET IMPLEMENTED.** This is a design spec to build from, not a description of existing code.
> **Audience:** any developer (or Claude session) picking up this feature — this doc is meant to be handed over as-is: "implement 05-workflow-node-checkpoints-plan.md exactly."
> Depends on the ranked workflow engine described in [03 — Runtime Pipeline](03-runtime-pipeline.md) (`IRankedWorkflowOrchestrator`, `WorkflowNode`, `WorkflowNodeCategory`, `Rank`/`SubRank`, `DefaultWorkflowNodeCatalog`).

## 1. Problem statement

Today a workflow (`WorkflowDefinition`) is triggered as one atomic unit — a launch URL (`LaunchContext(RouteId, WorkflowId)`, protected via `ILaunchTokenProtector`) always runs the **entire** DAG from source to every terminal node. There is no way to say "give me a link that only runs this workflow as far as node X and hands me back X's output," even though individual nodes (especially destinations, but potentially any node) each produce independently useful data mid-graph.

**Goal:** let a workflow designer flag *any* node — regardless of `WorkflowNodeCategory` or `Rank` — as a checkpoint. The system then exposes a shareable URL for that node. Hitting the URL:
1. Runs only the subgraph that node actually depends on (its ancestors) — never sibling branches, never downstream nodes.
2. Returns a run id.
3. A companion endpoint, given that run id, returns the checkpointed node's captured output.

This must work for **any** DAG shape a workflow happens to have (1 destination, 3 destinations fanned out, an analytics node consuming a destination's output, etc.) without new code per shape.

## 2. Design principles carried over from the design discussion

- **Per-node-instance opt-in, not per-node-type.** Eligibility is a boolean the workflow designer sets on a specific `WorkflowNode` row, not a rule baked into `WorkflowNodeCategory`/`DefaultWorkflowNodeCatalog`. A brand-new node type added to the catalog next year gets checkpoint support for free.
- **True graph reachability, not rank-threshold filtering.** "What must run to produce X's output" = the set of nodes reachable by walking `WorkflowEdge`s *backward* from X, not "everything with `Rank <= X.Rank`" (which incorrectly pulls in unrelated sibling branches once a workflow branches).
- **Reuse the existing launch-token mechanism** (`ILaunchTokenProtector` / `DataProtectionLaunchTokenProtector`) — same encrypted-opaque-token pattern already used for route/workflow launch URLs, just widened to optionally carry a node id.
- **No schema-breaking changes to `WorkflowRun`/`WorkflowNodeRun`** — a checkpoint run is a structurally normal run over a smaller (filtered) graph, not a new run type.

## 3. Phase 1 — core feature (recommended to build first; self-contained)

### 3.1 Domain — flag the node as checkpoint-eligible

File: `src/Runtime/FHIRBridge.Runtime.Domain/Workflows/WorkflowNode.cs`

Add a new constructor parameter / property, following the existing `IsEnabled` pattern:

```csharp
public bool CheckpointUrlEnabled { get; }
```

Threaded through the constructor exactly like `isEnabled` is today. Default `false` (opt-in).

### 3.2 Persistence

File: `src/FHIRBridge.Infrastructure/Persistence/Configurations/WorkflowPersistenceConfigurations.cs` (or wherever `WorkflowNode` is configured) — add a `bool CheckpointUrlEnabled` column, default `0`.

Migration: `AddWorkflowNodeCheckpointFlag` (follow the existing migration-naming convention seen in the repo, e.g. `AddPostLaunchRedirectUri`).

### 3.3 Launch context — carry an optional target node

File: `src/FHIRBridge.Application/Abstractions/Security/ILaunchTokenProtector.cs`

```csharp
public sealed record LaunchContext(Guid? RouteId, Guid? WorkflowId = null, Guid? TargetNodeId = null);
```

Add:
```csharp
/// <summary>Encrypts a (workflowId, targetNodeId) pair into an opaque checkpoint-launch token.</summary>
string ProtectWorkflowCheckpointContext(Guid workflowId, Guid targetNodeId);
```

Implement in `src/FHIRBridge.Infrastructure/Security/DataProtectionLaunchTokenProtector.cs` following the same `DataProtector.Protect`/pipe-delimited-payload pattern already used by `ProtectWorkflowContext`.

### 3.4 Orchestrator — ancestor-closure + partial execution

File: `src/Runtime/FHIRBridge.Runtime.Application/Workflows/RankedWorkflowOrchestrator.cs`

Add a pure, static helper (no dependencies, easily unit-testable in isolation):

```csharp
internal static WorkflowDefinition RestrictToAncestorClosure(WorkflowDefinition workflowDefinition, Guid targetNodeId)
{
    var nodesById = workflowDefinition.Nodes.ToDictionary(n => n.Id);
    var visited = new HashSet<Guid> { targetNodeId };
    var frontier = new Queue<Guid>();
    frontier.Enqueue(targetNodeId);

    while (frontier.Count > 0)
    {
        var current = frontier.Dequeue();
        foreach (var edge in workflowDefinition.Edges.Where(e => e.ToNodeId == current))
        {
            if (visited.Add(edge.FromNodeId))
            {
                frontier.Enqueue(edge.FromNodeId);
            }
        }
    }

    var restrictedNodes = workflowDefinition.Nodes.Where(n => visited.Contains(n.Id)).ToArray();
    var restrictedEdges = workflowDefinition.Edges
        .Where(e => visited.Contains(e.FromNodeId) && visited.Contains(e.ToNodeId))
        .ToArray();

    return workflowDefinition.WithNodesAndEdges(restrictedNodes, restrictedEdges); // add this helper to WorkflowDefinition if it doesn't already support projection
}
```

Extend `ExecuteAsync`'s signature (or add an overload) to accept an optional `Guid? targetNodeId`:

```csharp
public async Task<WorkflowRunResult> ExecuteAsync(
    WorkflowDefinition workflowDefinition,
    WorkflowExecutionContext context,
    Guid? targetNodeId = null,
    CancellationToken cancellationToken = default)
{
    var effectiveDefinition = targetNodeId is { } id
        ? RestrictToAncestorClosure(workflowDefinition, id)
        : workflowDefinition;

    // ...existing validation / TopologicalSort / foreach-execute logic runs unchanged against effectiveDefinition...
}
```

No change needed to `TopologicalSort`, `GetIncomingOutputs`, or the executor loop — they already operate purely off whatever `Nodes`/`Edges` they're given.

**Important:** validate `targetNodeId` exists in `workflowDefinition.Nodes` and is enabled *before* restricting, and validate the restricted graph through the normal `IWorkflowGraphValidator` — a partial graph must still pass cycle/contract checks.

### 3.5 API surface

File: `src/Api/FHIRBridge.Api/Workflows/WorkflowEndpoints.cs`

- `POST /api/v1/workflows/{workflowId}/nodes/{nodeId}/checkpoint-url` — validates `CheckpointUrlEnabled` is set on that node, calls `ILaunchTokenProtector.ProtectWorkflowCheckpointContext`, returns the full launch URL (mirrors how existing route/workflow launch URLs are already generated/copied in the builder UI).
- Existing `/oauth/launch/{context}` and workflow-trigger paths: when `UnprotectContext` yields a `TargetNodeId`, pass it through to `IRankedWorkflowOrchestrator.ExecuteAsync(..., targetNodeId: context.TargetNodeId, ...)`.
- Generalize the result-fetch endpoint. Today `GET /workflows/runs/{workflowRunId}/launch-result` hardcodes "first `ResourceBatch` payload" (source-node-shaped). Replace/extend with a lookup keyed by node contract, using the already-existing `IWorkflowNodeResourceHistoryRecorder.GetPagedAsync`:

```csharp
group.MapGet("/workflows/runs/{workflowRunId:guid}/checkpoint-result", async (
    Guid workflowRunId,
    IWorkflowRunStore runStore,
    IWorkflowNodeResourceHistoryRecorder recorder,
    CancellationToken cancellationToken) =>
{
    var run = await runStore.GetAsync(workflowRunId, cancellationToken); // needs WorkflowRun.TargetNodeId persisted (see 3.6)
    var payloads = await recorder.GetPagedAsync(workflowRunId, page: 1, pageSize: 50, cancellationToken);
    var nodePayload = payloads.Items
        .Where(p => run.TargetNodeId is null || /* match by node run -> node id */ true)
        .OrderByDescending(p => p.RecordedAtUtc)
        .FirstOrDefault();

    return nodePayload is null
        ? Results.Ok(new { result = (object?)null })
        : Results.Ok(new { result = JsonNode.Parse(nodePayload.PayloadJson), contract = nodePayload.Contract });
});
```

Anonymous endpoint, same as the existing `launch-result`, since it's reached via the same opaque-token launch flow.

### 3.6 Persist which node a run was scoped to

`WorkflowRun` (`Runtime.Domain/Workflows/WorkflowRun.cs`) gains `Guid? TargetNodeId` so the result-fetch endpoint (3.5) can resolve "what was this run a checkpoint for" from just the `workflowRunId` — the frontend never needs to pass a node id around.

### 3.7 Portal (workflow builder)

- Node config panel: add a "Enable Copy URL" toggle. Wire it through the same field-merge save path already fixed for the edit-preservation bug (`wizard.service.ts`, `workflow-build-assembler.service.ts`).
- When a node has the flag on, show a "Copy URL" action on the canvas node (parallel to however the existing route/workflow launch URL is copied today) that calls 3.5's `checkpoint-url` endpoint and copies the result to the clipboard.

### 3.8 Testing

- `FHIRBridge.Runtime.UnitTests`: unit-test `RestrictToAncestorClosure` directly against hand-built `WorkflowDefinition` fixtures covering: linear chain, fan-out (2+ destinations off one Mapping node), a node with no ancestors (a Source itself as target), and a diamond (two independent upstream branches merging into one node) — assert the restricted node/edge sets are exactly the expected ancestor set, and that sibling branches are excluded even when they share or beat the target's rank.
- `FHIRBridge.Api.IntegrationTests`: end-to-end — build a workflow with 2 destinations, flag one, hit the checkpoint URL, assert only the flagged destination's writer actually ran (e.g. via a spy/fake writer) and the other did not.

## 4. Phase 2 — revised: extend the catalog, don't relax the validator

**Original framing (superseded):** the first draft of this section proposed replacing `WorkflowGraphValidator.cs`'s rank-ordering check with cycle detection + a computed depth, on the theory that rank was the only thing standing between a sensible graph and a corrupted one. **That premise turned out to be wrong** — re-reading the actual validator surfaced two checks that already exist today, independent of rank:

```csharp
// WorkflowGraphValidator.cs:69 — the rank check this section originally proposed removing
if (toNode.Rank <= fromNode.Rank)
    errors.Add($"Edge from '{fromNode.NodeType}' to '{toNode.NodeType}' violates rank ordering.");

// lines 74-82 — a SEPARATE, already-existing contract check, evaluated regardless of the rank result above
if (!AreContractsCompatible(fromCatalog.OutputContract, toCatalog, toNode))
    errors.Add($"Node '{toNode.NodeType}' cannot accept output contract {fromCatalog.OutputContract} from '{fromNode.NodeType}'.");

// line 118 — cycle detection, also already independent of rank
if (HasCycle(enabledNodes.Keys, enabledEdges)) errors.Add("Workflow graph contains a cycle.");
```

`AreContractsCompatible` (same file) is strict for exactly the nodes that matter most — `Mapping` and any `Destination`/`Analytics` node require an **exact** `InputContracts` match, no fallback:

```csharp
if (toCatalog.InputContracts.Contains(fromContract)) return true;
if (toNode.NodeType == Mapping || toNode.Category is Destination or Analytics) return false; // strict, no exceptions
return IsFhirDataContract(fromContract) && toCatalog.InputContracts.Any(IsFhirDataContract);   // loose fallback, everyone else
```

Concretely: `SqlServerDestination → Mapping` is **already rejected today**, independent of rank — `fromContract` is `DestinationWriteResult`, `toNode.NodeType == Mapping` hits the strict branch, returns `false`. The corruption scenario this whole Phase 2 discussion was worried about doesn't require new validator work; it's already handled.

### 4.1 Why the rank check should stay, not be deleted

The loose fallback branch above (`IsFhirDataContract(...) && ...Any(IsFhirDataContract)`) is deliberately permissive for the Compliance/Transform family that all traffic in FHIR-batch shapes — `Consent`, `UsCoreValidation`, `Normalization`, `Terminology*`, `DeIdentification`. Any one of these can feed any other via the loose branch, **regardless of pipeline stage** — that fallback doesn't know or care about intended ordering. Today, the rank-ordering check (`toNode.Rank <= fromNode.Rank`) is the *only* thing preventing a semantically-backward-but-type-safe edge like `Terminology → Consent` or `DeIdentification → Normalization`. Deleting the rank check wholesale (the original 4.1 proposal) would reopen exactly that gap for this node family — not data-type corruption, but silently wrong pipeline sequencing, which is arguably just as bad. **Keep `WorkflowGraphValidator.cs` and `RankedWorkflowOrchestrator.TopologicalSort` untouched.**

### 4.2 How to actually support a new chained capability (e.g. destination → notifier)

When a concrete need shows up — e.g. "notify a webhook once `SqlServerDestination` finishes writing" — satisfy it by adding a **new catalog entry with its own rank tier**, the same way `AuditLineage` already breaks from the generic per-category helper to claim a bespoke rank (`80`, distinct from the other Compliance ranks `10/20/50`) in `DefaultWorkflowNodeCatalog.cs`. Example:

```csharp
new(
    "webhook-notifier",
    WorkflowNodeCategory.Destination,
    71, // one above SqlServerDestination/CsvDestination's rank 70 — not the generic Destination() helper's fixed 70
    [],
    [WorkflowDataContract.DestinationWriteResult],
    WorkflowDataContract.DestinationWriteResult,
    "webhook-notifier",
    "HTTP Notify (Webhook)",
    "http-notify",
    "Notify a webhook once an upstream destination finishes writing.")
```

With this entry:
- The existing rank check passes naturally (`71 > 70`) — no validator change.
- The existing contract check passes naturally, and does so **strictly** — this node's `Category` is `Destination`, so `AreContractsCompatible` requires an exact match against `InputContracts: [DestinationWriteResult]`; the loose FHIR fallback never applies to it. `SqlServerDestination → webhook-notifier` is accepted; `webhook-notifier → Mapping` or anything expecting a FHIR-shaped batch is still rejected, exactly like `SqlServerDestination → Mapping` is today.
- Zero changes to `WorkflowGraphValidator.cs`, `WorkflowRankPolicy.cs`, or `RankedWorkflowOrchestrator.cs`. This is a purely additive `DefaultWorkflowNodeCatalog.cs` change — the same "new class/entry, never a switch or core-logic edit" pattern this codebase already uses for vendor connectors and `ApplicationType` strategies.

This replaces the original 4.2's "widen `Mapping`'s contracts or add a new node type" framing — the new-node-type path is confirmed as the right one, it's just cheaper than originally scoped (no widened contracts anywhere, no orchestrator changes) because the validator was already doing the safety-critical part.

## 5. Suggested sequencing

| Step | Scope | Depends on |
|---|---|---|
| 1 | 3.1–3.2 (domain flag + migration) | — |
| 2 | 3.3–3.4 (launch context + orchestrator ancestor-closure) | 1 |
| 3 | 3.5–3.6 (API endpoints + `WorkflowRun.TargetNodeId`) | 2 |
| 4 | 3.7 (portal toggle + Copy URL button) | 3 |
| 5 | 3.8 (tests) | in parallel with 2–4 |
| 6 | Phase 2 (§4.2 — add a new catalog entry with its own rank tier for the specific new node type needed) | only if/when a concrete chained-node need is actually requested; §4.1's "leave the validator alone" is a standing decision, not a task |

## 6. Visual references (static, client-side demos)

Two standalone HTML files under `docs/backend/prototypes/` let you click through the behavior described above before any backend code exists. Both are pure HTML/CSS/JS — open either directly in a browser, no build step or server required — and every URL/token/run-id shown is simulated client-side.

- **[prototypes/workflow-checkpoint-phase1.html](prototypes/workflow-checkpoint-phase1.html)** — Phase 1 only. Hover any node → 🚩 enables a checkpoint on it → 🔗 opens a modal showing a generated Copy URL, a simulated run id, and the exact ordered list of ancestor nodes that would execute — with those nodes highlighted (and everything else dimmed) directly on the canvas. Demonstrates that siblings and downstream nodes never run, on whatever shape you build (fan-out to multiple destinations, merges, etc.).
- **[prototypes/workflow-checkpoint-phase1-and-2.html](prototypes/workflow-checkpoint-phase1-and-2.html)** — Phase 1 + a Phase 2 exploration. Adds a manual port-drag connection rule based on cycle detection *plus* a hard contract-compatibility gate (not rank) — dragging `SQL Server → Mapping` or `HTTP Notify → Mapping` is blocked with a "type mismatch" toast regardless of rank, while `SQL Server → HTTP Notify` is allowed because the demo gives `HTTP Notify` an `inputContracts` of `DestinationWriteResult`. A teal `D#` badge per node shows depth computed live from the graph edges.

**Important divergence from §4's final recommendation:** the demo illustrates cycle-check + contract-check *replacing* the rank rule entirely (a general-purpose relaxation), which is what surfaced the corruption risk in the first place and is useful for seeing the failure mode concretely. §4 above lands somewhere more conservative for the real backend: **keep the rank check as-is** (it's the only thing guarding the Compliance/Transform family's loose contract fallback from backward reordering) and add new capabilities as new catalog entries with their own rank tier instead. Treat the demo as "here's what happens if you relax rank without also being careful about the loose-fallback gap," not as the literal thing to build.

## 7. Open questions to confirm before/during implementation

1. Should a checkpoint run be visually distinguished from a full run in the Execution History screen (e.g. a "Partial (Node: X)" badge), or treated identically?
2. Should `CheckpointUrlEnabled` require the node to have `IsEnabled == true`, or can a disabled node still be checkpoint-triggered independently?
3. Rate limiting / auth on checkpoint URLs — same anonymous-token model as today's route/workflow launch URLs, or should checkpoint URLs get their own scoping (e.g. per-destination API keys) given they's likely to be handed to external end users per the original "different audiences want different destinations" use case?
