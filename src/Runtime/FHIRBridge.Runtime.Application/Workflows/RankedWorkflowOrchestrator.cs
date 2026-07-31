using System.Text.Json;
using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Workflows.Audit;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class RankedWorkflowOrchestrator : IRankedWorkflowOrchestrator
{
    private readonly IWorkflowGraphValidator _graphValidator;
    private readonly IWorkflowNodeExecutorRegistry _executorRegistry;
    private readonly IWorkflowAuditRecorder _auditRecorder;
    private readonly IWorkflowRunStore? _runStore;
    private readonly IWorkflowNodeResourceHistoryRecorder? _resourceHistoryRecorder;
    private readonly IGlobalExceptionManager? _exceptionManager;
    private readonly IRunStatusNotifier? _runStatusNotifier;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IWorkflowDefinitionStore? _workflowDefinitionStore;
    private readonly IBulkExportPauseRecorder? _bulkExportPauseRecorder;

    public RankedWorkflowOrchestrator(
        IWorkflowGraphValidator graphValidator,
        IWorkflowNodeExecutorRegistry executorRegistry,
        IWorkflowAuditRecorder? auditRecorder = null,
        IWorkflowRunStore? runStore = null,
        IWorkflowNodeResourceHistoryRecorder? resourceHistoryRecorder = null,
        IGlobalExceptionManager? exceptionManager = null,
        IRunStatusNotifier? runStatusNotifier = null,
        IServiceScopeFactory? scopeFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IBulkExportPauseRecorder? bulkExportPauseRecorder = null)
    {
        _graphValidator = graphValidator;
        _executorRegistry = executorRegistry;
        _auditRecorder = auditRecorder ?? new InMemoryWorkflowAuditRecorder();
        _runStore = runStore;
        _resourceHistoryRecorder = resourceHistoryRecorder;
        _exceptionManager = exceptionManager;
        _runStatusNotifier = runStatusNotifier;
        _scopeFactory = scopeFactory;
        _workflowDefinitionStore = workflowDefinitionStore;
        _bulkExportPauseRecorder = bulkExportPauseRecorder;
    }

    public Task<WorkflowRunResult> ExecuteAsync(
        WorkflowDefinition workflowDefinition,
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(workflowDefinition, context, targetNodeId: null, cancellationToken);

    /// <summary>Runs the full graph when <paramref name="targetNodeId"/> is null; otherwise restricts execution to
    /// that node's ancestor closure (see <see cref="RestrictToAncestorClosure"/>) — a checkpoint run. The resulting
    /// <see cref="WorkflowRun"/> records <paramref name="targetNodeId"/> so a checkpoint result can be resolved
    /// later from just the run id.</summary>
    public async Task<WorkflowRunResult> ExecuteAsync(
        WorkflowDefinition workflowDefinition,
        WorkflowExecutionContext context,
        Guid? targetNodeId,
        CancellationToken cancellationToken = default)
    {
        if (targetNodeId is { } requestedTargetNodeId
            && workflowDefinition.Nodes.All(node => node.Id != requestedTargetNodeId))
        {
            throw new ArgumentException(
                $"Node '{requestedTargetNodeId}' does not exist in this workflow.", nameof(targetNodeId));
        }

        var effectiveDefinition = targetNodeId is { } id
            ? RestrictToAncestorClosure(workflowDefinition, id)
            : workflowDefinition;

        var validationResult = _graphValidator.Validate(effectiveDefinition);
        if (!validationResult.IsValid)
        {
            throw new WorkflowGraphValidationException(validationResult.Errors);
        }

        var workflowRun = new WorkflowRun(
            context.WorkflowRunId,
            workflowDefinition.Id,
            DateTimeOffset.UtcNow,
            context.TriggeredBy,
            context.TriggerType,
            targetNodeId,
            workflowDefinitionVersion: workflowDefinition.Version,
            correlationId: context.CorrelationId);
        var orderedNodes = TopologicalSort(effectiveDefinition);
        var outputsByNodeId = new Dictionary<Guid, WorkflowNodeOutput>();
        var skippedResourceTypesAcrossRun = new List<string>();

        // Persist a "Running" row up front (rather than only ever writing this run once it reaches a terminal
        // state) so a run genuinely appears as Running in the Dashboard/Workflow List — and to any client — for
        // its entire in-flight duration, not just retroactively once it finishes. SqlWorkflowRunStore.SaveAsync
        // replaces this placeholder wholesale with the fully-populated terminal aggregate once Succeed()/Fail()
        // is called below. Best-effort: a transient failure here must never abort the run itself.
        await PersistRunStartedAsync(workflowRun, cancellationToken);
        await NotifyRunStatusAsync(workflowRun.Id, workflowDefinition.Id, "Running", DateTimeOffset.UtcNow, null, cancellationToken);

        await _auditRecorder.RecordAsync(new(
            WorkflowAuditEventType.WorkflowRunStarted,
            workflowDefinition.Id,
            workflowRun.Id,
            null,
            null,
            null,
            null,
            WorkflowDataContract.None,
            WorkflowDataContract.None,
            DateTimeOffset.UtcNow), cancellationToken);

        return await RunNodesAsync(
            workflowDefinition, effectiveDefinition, workflowRun, context, orderedNodes,
            outputsByNodeId, skippedResourceTypesAcrossRun, cancellationToken);
    }

    /// <summary>Resumes a run that <see cref="RunNodesAsync"/> paused at <paramref name="nodeId"/> (a source node
    /// that deferred to an async bulk-export job — see <see cref="WorkflowRunStatus.AwaitingBulkExport"/>). Called
    /// by <c>BulkExportPollWorker</c> once the job completes. Reconstructs everything the original in-memory
    /// <see cref="ExecuteAsync"/> call held — the execution context, and every node output computed before the
    /// pause — from what was persisted onto the <c>BulkExportJob</c> row at pause time, since none of it survives
    /// as ambient/in-memory state across a Worker tick boundary (this may run in an entirely different process).
    /// <para>Only supports one pause per run: if a node further downstream also defers, that second deferral is
    /// treated as an ordinary in-loop pause (handled the same way by <see cref="RunNodesAsync"/>) — the run pauses
    /// again and needs a second resume call, so multiple sequential bulk-export nodes in one run each get their own
    /// resume cycle rather than needing to be anticipated here.</para></summary>
    public async Task<WorkflowRunResult> ResumeAfterBulkExportAsync(
        Guid workflowRunId,
        Guid nodeId,
        string? priorNodeOutputsJson,
        string? contextJson,
        IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> resources,
        CancellationToken cancellationToken = default)
    {
        if (_runStore is null)
        {
            throw new InvalidOperationException("No IWorkflowRunStore configured; cannot resume a paused run.");
        }

        if (_workflowDefinitionStore is null)
        {
            throw new InvalidOperationException("No IWorkflowDefinitionStore configured; cannot resume a paused run.");
        }

        var workflowRun = await _runStore.GetAsync(workflowRunId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow run '{workflowRunId}' was not found.");
        var workflowDefinition = await _workflowDefinitionStore.GetAsync(workflowRun.WorkflowDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow definition '{workflowRun.WorkflowDefinitionId}' was not found.");

        var effectiveDefinition = workflowRun.TargetNodeId is { } targetId
            ? RestrictToAncestorClosure(workflowDefinition, targetId)
            : workflowDefinition;

        var pausedNode = effectiveDefinition.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"Node '{nodeId}' was not found on workflow '{workflowDefinition.Id}'.");

        var context = DeserializeExecutionContext(contextJson, workflowRunId);
        var orderedNodes = TopologicalSort(effectiveDefinition);
        var outputsByNodeId = DeserializePriorNodeOutputs(priorNodeOutputsJson);
        var skippedResourceTypesAcrossRun = new List<string>();

        var materializedOutput = new WorkflowNodeOutput(
            pausedNode.Id,
            pausedNode.NodeType,
            new FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceBatch(ToPayloadEnvelopes(resources)),
            WorkflowDataContract.ResourceBatch,
            new Dictionary<string, object?> { ["executor"] = "BulkExportPollWorker", ["count"] = resources.Count });
        outputsByNodeId[pausedNode.Id] = materializedOutput;

        var resumedNodeRun = new WorkflowNodeRun(
            Guid.NewGuid(), workflowRun.Id, pausedNode.Id, pausedNode.NodeType, pausedNode.Rank, pausedNode.SubRank, DateTimeOffset.UtcNow);
        workflowRun.AddNodeRun(resumedNodeRun);
        resumedNodeRun.Succeed(CreateLineageJson(pausedNode, [], materializedOutput), DateTimeOffset.UtcNow);

        if (_resourceHistoryRecorder is not null)
        {
            await _resourceHistoryRecorder.RecordNodeOutputAsync(
                workflowRun.Id, resumedNodeRun.Id, pausedNode.NodeType,
                materializedOutput.Contract.ToString(), materializedOutput.Payload, cancellationToken);
        }

        var remainingNodes = orderedNodes.SkipWhile(n => n.Id != pausedNode.Id).Skip(1).ToArray();

        return await RunNodesAsync(
            workflowDefinition, effectiveDefinition, workflowRun, context, remainingNodes,
            outputsByNodeId, skippedResourceTypesAcrossRun, cancellationToken);
    }

    /// <summary>Runs <paramref name="nodesToRun"/> in order against <paramref name="workflowRun"/>, shared by both a
    /// fresh <see cref="ExecuteAsync"/> call and <see cref="ResumeAfterBulkExportAsync"/> picking up after a pause —
    /// so the success/partial-success/cancel/fail handling and persistence stay identical for both entry points.</summary>
    private async Task<WorkflowRunResult> RunNodesAsync(
        WorkflowDefinition workflowDefinition,
        WorkflowDefinition effectiveDefinition,
        WorkflowRun workflowRun,
        WorkflowExecutionContext context,
        IReadOnlyCollection<WorkflowNode> nodesToRun,
        Dictionary<Guid, WorkflowNodeOutput> outputsByNodeId,
        List<string> skippedResourceTypesAcrossRun,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var node in nodesToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var incomingOutputs = GetIncomingOutputs(effectiveDefinition, node, outputsByNodeId);
                var inputContract = incomingOutputs.FirstOrDefault()?.Contract ?? WorkflowDataContract.None;
                var nodeRun = new WorkflowNodeRun(
                    Guid.NewGuid(),
                    workflowRun.Id,
                    node.Id,
                    node.NodeType,
                    node.Rank,
                    node.SubRank,
                    DateTimeOffset.UtcNow);

                await _auditRecorder.RecordAsync(new(
                    WorkflowAuditEventType.NodeExecutionStarted,
                    workflowDefinition.Id,
                    workflowRun.Id,
                    node.Id,
                    node.NodeType,
                    null,
                    null,
                    inputContract,
                    WorkflowDataContract.None,
                    DateTimeOffset.UtcNow), cancellationToken);

                try
                {
                    var executor = _executorRegistry.GetRequired(node.NodeType);
                    var output = await executor.ExecuteAsync(context, node, incomingOutputs, cancellationToken);

                    if (output.Metadata.TryGetValue(WorkflowNodeOutputMetadataKeys.BulkExportDeferredJobId, out var deferredJobIdValue)
                        && deferredJobIdValue is string deferredJobIdText
                        && Guid.TryParse(deferredJobIdText, out var deferredJobId))
                    {
                        // Paused: this node's run isn't added to workflowRun's history yet — it isn't done — a
                        // resume adds its terminal (Succeeded) run once the deferred job's output is materialized.
                        // Persist everything computed so far (every prior node's output) so a resume, quite
                        // possibly in a different Worker process, can seed outputsByNodeId without recomputing it.
                        if (_bulkExportPauseRecorder is not null)
                        {
                            await _bulkExportPauseRecorder.RecordPauseAsync(
                                deferredJobId, SerializePriorNodeOutputs(outputsByNodeId), cancellationToken);
                        }

                        workflowRun.AwaitBulkExport();
                        await _auditRecorder.RecordAsync(new(
                            WorkflowAuditEventType.WorkflowRunAwaitingBulkExport,
                            workflowDefinition.Id,
                            workflowRun.Id,
                            node.Id,
                            node.NodeType,
                            null,
                            null,
                            inputContract,
                            WorkflowDataContract.None,
                            DateTimeOffset.UtcNow), cancellationToken);
                        await PersistRunAsync(workflowRun, cancellationToken);
                        await NotifyRunStatusAsync(
                            workflowRun.Id, workflowDefinition.Id, "AwaitingBulkExport", DateTimeOffset.UtcNow, null, cancellationToken);

                        return new WorkflowRunResult(workflowRun, outputsByNodeId);
                    }

                    outputsByNodeId[node.Id] = output;
                    workflowRun.AddNodeRun(nodeRun);
                    nodeRun.Succeed(CreateLineageJson(node, incomingOutputs, output), DateTimeOffset.UtcNow);

                    if (output.Metadata.TryGetValue("skippedResourceTypes", out var skippedValue)
                        && skippedValue is string[] { Length: > 0 } skippedReasons)
                    {
                        skippedResourceTypesAcrossRun.AddRange(skippedReasons);
                    }

                    if (_resourceHistoryRecorder is not null && output.Contract != WorkflowDataContract.None)
                    {
                        await _resourceHistoryRecorder.RecordNodeOutputAsync(
                            workflowRun.Id,
                            nodeRun.Id,
                            node.NodeType,
                            output.Contract.ToString(),
                            output.Payload,
                            cancellationToken);
                    }

                    await _auditRecorder.RecordAsync(new(
                        WorkflowAuditEventType.NodeExecutionCompleted,
                        workflowDefinition.Id,
                        workflowRun.Id,
                        node.Id,
                        node.NodeType,
                        null,
                        null,
                        inputContract,
                        output.Contract,
                        DateTimeOffset.UtcNow), cancellationToken);
                }
                catch (FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException cancelException)
                {
                    workflowRun.AddNodeRun(nodeRun);
                    nodeRun.Cancel(cancelException.Message, DateTimeOffset.UtcNow);
                    await _auditRecorder.RecordAsync(new(
                        WorkflowAuditEventType.NodeExecutionCancelled,
                        workflowDefinition.Id,
                        workflowRun.Id,
                        node.Id,
                        node.NodeType,
                        null,
                        null,
                        inputContract,
                        WorkflowDataContract.None,
                        DateTimeOffset.UtcNow,
                        cancelException.Message), cancellationToken);
                    throw;
                }
                catch (Exception exception)
                {
                    workflowRun.AddNodeRun(nodeRun);
                    nodeRun.Fail(exception.Message, DateTimeOffset.UtcNow);
                    await _auditRecorder.RecordAsync(new(
                        WorkflowAuditEventType.NodeExecutionFailed,
                        workflowDefinition.Id,
                        workflowRun.Id,
                        node.Id,
                        node.NodeType,
                        null,
                        null,
                        inputContract,
                        WorkflowDataContract.None,
                        DateTimeOffset.UtcNow,
                        exception.Message), cancellationToken);
                    throw;
                }
            }

            if (skippedResourceTypesAcrossRun.Count > 0)
            {
                var summary = "Partial success — some resource types were skipped because this app is not " +
                    $"authorized for them: {string.Join(" | ", skippedResourceTypesAcrossRun)}";
                workflowRun.PartialSucceed(summary, DateTimeOffset.UtcNow);

                // Unlike Cancel/Fail, reaching here throws nothing — this branch is a normal completion path, so
                // without an explicit capture call the skipped-resource-type reason only ever lands in
                // WorkflowRun.ErrorMessage. Correlation Search's Workflow Runs table doesn't render that field, so
                // the only place this becomes visible there is the dedicated Errors section — same place Cancel's
                // scope-authorization reason already shows up, via the same CaptureExpectedAsync/Informational path.
                if (_exceptionManager is not null)
                {
                    await _exceptionManager.CaptureExpectedAsync(
                        new ExpectedFailure("PartialSuccess", summary),
                        new ExceptionContext(
                            Module: "Workflow",
                            Severity: "Informational",
                            CorrelationId: context.CorrelationId,
                            WorkflowId: workflowDefinition.Id.ToString(),
                            ExecutionId: workflowRun.Id.ToString()),
                        cancellationToken);
                }
            }
            else
            {
                workflowRun.Succeed(DateTimeOffset.UtcNow);
            }

            await _auditRecorder.RecordAsync(new(
                WorkflowAuditEventType.WorkflowRunCompleted,
                workflowDefinition.Id,
                workflowRun.Id,
                null,
                null,
                null,
                null,
                WorkflowDataContract.None,
                WorkflowDataContract.None,
                DateTimeOffset.UtcNow), cancellationToken);

            await PersistRunAsync(workflowRun, cancellationToken);
            await NotifyRunStatusAsync(
                workflowRun.Id, workflowDefinition.Id, workflowRun.Status.ToString(), DateTimeOffset.UtcNow, workflowRun.ErrorMessage, cancellationToken);
        }
        catch (FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException cancelException)
        {
            workflowRun.Cancel(cancelException.Message, DateTimeOffset.UtcNow);
            await _auditRecorder.RecordAsync(new(
                WorkflowAuditEventType.WorkflowRunCancelled,
                workflowDefinition.Id,
                workflowRun.Id,
                null,
                null,
                null,
                null,
                WorkflowDataContract.None,
                WorkflowDataContract.None,
                DateTimeOffset.UtcNow,
                cancelException.Message), cancellationToken);

            try
            {
                await PersistRunAsync(workflowRun, CancellationToken.None);
            }
            catch
            {
                // Swallowed by design — see the matching remark in the generic failure branch below.
            }

            await NotifyRunStatusAsync(workflowRun.Id, workflowDefinition.Id, "Cancelled", DateTimeOffset.UtcNow, cancelException.Message, CancellationToken.None);

            if (_exceptionManager is not null)
            {
                await _exceptionManager.CaptureExpectedAsync(
                    new ExpectedFailure(nameof(FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException), cancelException.Message),
                    new ExceptionContext(
                        Module: "Workflow",
                        Severity: "Informational",
                        CorrelationId: context.CorrelationId,
                        WorkflowId: workflowDefinition.Id.ToString(),
                        ExecutionId: workflowRun.Id.ToString()),
                    CancellationToken.None);
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            workflowRun.Fail(exception.Message, DateTimeOffset.UtcNow);
            await _auditRecorder.RecordAsync(new(
                WorkflowAuditEventType.WorkflowRunFailed,
                workflowDefinition.Id,
                workflowRun.Id,
                null,
                null,
                null,
                null,
                WorkflowDataContract.None,
                WorkflowDataContract.None,
                DateTimeOffset.UtcNow,
                exception.Message), cancellationToken);

            // Persist the failed run with its partial node-run timeline. Use None so the history is captured
            // even when the caller's token is the reason the run aborted. Best-effort: a store failure here
            // (e.g. a transient DB error) must not also suppress the "Failed" SignalR notify below, or the run
            // would be stuck showing "Running" both in the DB and to any live client.
            try
            {
                await PersistRunAsync(workflowRun, CancellationToken.None);
            }
            catch
            {
                // Swallowed by design — see the remark above; NotifyRunStatusAsync still runs regardless.
            }

            await NotifyRunStatusAsync(workflowRun.Id, workflowDefinition.Id, "Failed", DateTimeOffset.UtcNow, exception.Message, CancellationToken.None);

            if (_exceptionManager is not null)
            {
                await _exceptionManager.CaptureAsync(
                    exception,
                    new ExceptionContext(
                        Module: "Workflow",
                        CorrelationId: context.CorrelationId,
                        WorkflowId: workflowDefinition.Id.ToString(),
                        ExecutionId: workflowRun.Id.ToString()),
                    CancellationToken.None);
            }

            throw;
        }

        return new WorkflowRunResult(workflowRun, outputsByNodeId);
    }

    private Task PersistRunAsync(WorkflowRun workflowRun, CancellationToken cancellationToken)
        => _runStore is null
            ? Task.CompletedTask
            : _runStore.SaveAsync(workflowRun, cancellationToken);

    /// <summary>Best-effort initial persist of the run while it's still Running (see the call site above) — swallows
    /// a transient store failure rather than letting it abort the run, since the terminal <see cref="PersistRunAsync"/>
    /// call is what actually guarantees this run's history is captured either way.</summary>
    /// <remarks>
    /// Deliberately does NOT use <see cref="_runStore"/> when a scope factory is available. That instance (and its
    /// DbContext) is the SAME one this orchestrator, its executors, and every other DI-resolved dependency reuse for
    /// the rest of this run — writing this placeholder through it left the DbContext holding a tracked
    /// <see cref="WorkflowRun"/> aggregate for the run's entire in-flight duration, which caused later node
    /// execution to hang indefinitely (reproduced and bisected against this exact call). A fresh, short-lived scope
    /// keeps this one write fully isolated, so its DbContext is created, used, and disposed before node execution
    /// ever starts.
    /// </remarks>
    private async Task PersistRunStartedAsync(WorkflowRun workflowRun, CancellationToken cancellationToken)
    {
        if (_runStore is null)
        {
            return;
        }

        try
        {
            if (_scopeFactory is not null)
            {
                using var scope = _scopeFactory.CreateScope();
                var scopedRunStore = scope.ServiceProvider.GetService<IWorkflowRunStore>();
                if (scopedRunStore is not null)
                {
                    await scopedRunStore.SaveAsync(workflowRun, cancellationToken);
                    return;
                }
            }

            await _runStore.SaveAsync(workflowRun, cancellationToken);
        }
        catch
        {
            // Swallowed by design — see the XML doc above.
        }
    }

    /// <summary>Best-effort live push (see <see cref="IRunStatusNotifier"/>) — a broadcast failure must never
    /// affect the run itself, so any exception here is swallowed rather than propagated.</summary>
    private async Task NotifyRunStatusAsync(
        Guid workflowRunId,
        Guid workflowDefinitionId,
        string status,
        DateTimeOffset occurredAt,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (_runStatusNotifier is null)
        {
            return;
        }

        try
        {
            await _runStatusNotifier.NotifyAsync(
                new RunStatusChangedEvent(workflowRunId, workflowDefinitionId, status, occurredAt, errorMessage),
                cancellationToken);
        }
        catch
        {
            // Swallowed by design — see the XML doc above.
        }
    }

    /// <summary>Projects <paramref name="workflowDefinition"/> down to the subgraph <paramref name="targetNodeId"/>
    /// actually depends on — true backward reachability over edges, not a rank threshold (which would incorrectly
    /// pull in unrelated sibling branches once a workflow branches). See
    /// docs/backend/05-workflow-node-checkpoints-plan.md §3.4.</summary>
    internal static WorkflowDefinition RestrictToAncestorClosure(WorkflowDefinition workflowDefinition, Guid targetNodeId)
    {
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

        return workflowDefinition.WithNodesAndEdges(restrictedNodes, restrictedEdges);
    }

    private static IReadOnlyCollection<WorkflowNodeOutput> GetIncomingOutputs(
        WorkflowDefinition workflowDefinition,
        WorkflowNode node,
        IReadOnlyDictionary<Guid, WorkflowNodeOutput> outputsByNodeId)
        => workflowDefinition.Edges
            .Where(edge => edge.ToNodeId == node.Id)
            .Select(edge => outputsByNodeId.TryGetValue(edge.FromNodeId, out var output) ? output : null)
            .OfType<WorkflowNodeOutput>()
            .ToArray();

    private static IReadOnlyCollection<WorkflowNode> TopologicalSort(WorkflowDefinition workflowDefinition)
    {
        var nodes = workflowDefinition.Nodes
            .Where(node => node.IsEnabled)
            .ToDictionary(node => node.Id);
        var edges = workflowDefinition.Edges
            .Where(edge => nodes.ContainsKey(edge.FromNodeId) && nodes.ContainsKey(edge.ToNodeId))
            .ToArray();

        var outgoing = nodes.Keys.ToDictionary(id => id, _ => new List<Guid>());
        var indegree = nodes.Keys.ToDictionary(id => id, _ => 0);

        foreach (var edge in edges)
        {
            outgoing[edge.FromNodeId].Add(edge.ToNodeId);
            indegree[edge.ToNodeId]++;
        }

        var ready = nodes.Values
            .Where(node => indegree[node.Id] == 0)
            .OrderBy(node => node.Rank)
            .ThenBy(node => node.SubRank)
            .ThenBy(node => node.NodeType, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ordered = new List<WorkflowNode>();

        while (ready.Count > 0)
        {
            var node = ready[0];
            ready.RemoveAt(0);
            ordered.Add(node);

            foreach (var downstreamNodeId in outgoing[node.Id])
            {
                indegree[downstreamNodeId]--;

                if (indegree[downstreamNodeId] == 0)
                {
                    ready.Add(nodes[downstreamNodeId]);
                    ready.Sort(CompareNodes);
                }
            }
        }

        return ordered;
    }

    private static int CompareNodes(WorkflowNode left, WorkflowNode right)
    {
        var rankComparison = left.Rank.CompareTo(right.Rank);
        if (rankComparison != 0)
        {
            return rankComparison;
        }

        var subRankComparison = left.SubRank.CompareTo(right.SubRank);
        return subRankComparison != 0
            ? subRankComparison
            : string.Compare(left.NodeType, right.NodeType, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateLineageJson(
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        WorkflowNodeOutput output)
        => JsonSerializer.Serialize(new
        {
            nodeId = node.Id,
            node.NodeType,
            inputContracts = inputs.Select(input => input.Contract.ToString()).ToArray(),
            inputNodeIds = inputs.Select(input => input.NodeId).ToArray(),
            outputNodeId = output.NodeId,
            outputContract = output.Contract.ToString(),
            outputMetadata = output.Metadata
        });

    /// <summary>Snapshot of one already-computed node's output, for round-tripping across the pause/resume
    /// boundary. Only <see cref="WorkflowDataContract.ResourceBatch"/> payloads are actually persisted — every node
    /// that can run before a source node defers is itself a source node (per the pipeline's fixed
    /// Extraction → Governance → Transform → Output topology, extraction nodes have no dependency on each other),
    /// so <c>ResourceBatch</c> is the only payload shape that can ever appear here in practice. Anything else is
    /// kept as metadata-only (no generic way to round-trip an arbitrary <c>object?</c> payload through JSON).</summary>
    private sealed record PriorNodeOutputSnapshot(
        string NodeType,
        string Contract,
        IReadOnlyDictionary<string, object?> Metadata,
        FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope[]? Resources);

    private static FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope[] ToPayloadEnvelopes(
        IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> resources)
        => resources
            .Select(resource => new FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope(
                resource.ResourceType, resource.ResourceId ?? string.Empty, resource.RawJson))
            .ToArray();

    private static string SerializePriorNodeOutputs(IReadOnlyDictionary<Guid, WorkflowNodeOutput> outputsByNodeId)
    {
        var snapshot = outputsByNodeId.ToDictionary(
            pair => pair.Key.ToString(),
            pair => new PriorNodeOutputSnapshot(
                pair.Value.NodeType,
                pair.Value.Contract.ToString(),
                pair.Value.Metadata,
                pair.Value.Contract == WorkflowDataContract.ResourceBatch
                    && pair.Value.Payload is FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceBatch resourceBatch
                        ? resourceBatch.Resources.ToArray()
                        : null));

        return JsonSerializer.Serialize(snapshot);
    }

    private static Dictionary<Guid, WorkflowNodeOutput> DeserializePriorNodeOutputs(string? priorNodeOutputsJson)
    {
        var outputsByNodeId = new Dictionary<Guid, WorkflowNodeOutput>();
        if (string.IsNullOrWhiteSpace(priorNodeOutputsJson))
        {
            return outputsByNodeId;
        }

        var snapshot = JsonSerializer.Deserialize<Dictionary<string, PriorNodeOutputSnapshot>>(priorNodeOutputsJson);
        if (snapshot is null)
        {
            return outputsByNodeId;
        }

        foreach (var (nodeIdText, entry) in snapshot)
        {
            if (!Guid.TryParse(nodeIdText, out var nodeId))
            {
                continue;
            }

            var contract = Enum.TryParse<WorkflowDataContract>(entry.Contract, out var parsedContract)
                ? parsedContract
                : WorkflowDataContract.None;
            object? payload = entry.Resources is { } resources
                ? new FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceBatch(resources)
                : null;

            outputsByNodeId[nodeId] = new WorkflowNodeOutput(nodeId, entry.NodeType, payload, contract, entry.Metadata);
        }

        return outputsByNodeId;
    }

    private sealed record SerializedExecutionContext(
        Guid WorkflowRunId,
        string CorrelationId,
        string? TriggeredBy,
        string? TriggerType,
        string? TargetPatientId,
        string? PatientSearchCriteria,
        string? CallerId);

    private static WorkflowExecutionContext DeserializeExecutionContext(string? contextJson, Guid fallbackWorkflowRunId)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return new WorkflowExecutionContext(fallbackWorkflowRunId, fallbackWorkflowRunId.ToString("N"));
        }

        var dto = JsonSerializer.Deserialize<SerializedExecutionContext>(contextJson)
            ?? new SerializedExecutionContext(fallbackWorkflowRunId, fallbackWorkflowRunId.ToString("N"), null, null, null, null, null);

        return new WorkflowExecutionContext(
            dto.WorkflowRunId,
            dto.CorrelationId,
            properties: null,
            dto.TriggeredBy,
            dto.TriggerType,
            dto.TargetPatientId,
            dto.PatientSearchCriteria,
            dto.CallerId);
    }
}
