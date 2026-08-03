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

    public RankedWorkflowOrchestrator(
        IWorkflowGraphValidator graphValidator,
        IWorkflowNodeExecutorRegistry executorRegistry,
        IWorkflowAuditRecorder? auditRecorder = null,
        IWorkflowRunStore? runStore = null,
        IWorkflowNodeResourceHistoryRecorder? resourceHistoryRecorder = null,
        IGlobalExceptionManager? exceptionManager = null,
        IRunStatusNotifier? runStatusNotifier = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _graphValidator = graphValidator;
        _executorRegistry = executorRegistry;
        _auditRecorder = auditRecorder ?? new InMemoryWorkflowAuditRecorder();
        _runStore = runStore;
        _resourceHistoryRecorder = resourceHistoryRecorder;
        _exceptionManager = exceptionManager;
        _runStatusNotifier = runStatusNotifier;
        _scopeFactory = scopeFactory;
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

        try
        {
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

            foreach (var node in orderedNodes)
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

                workflowRun.AddNodeRun(nodeRun);
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
                    outputsByNodeId[node.Id] = output;
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
}
