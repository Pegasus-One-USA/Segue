using System.Text.Json;
using FHIRBridge.Runtime.Application.Workflows.Audit;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class RankedWorkflowOrchestrator : IRankedWorkflowOrchestrator
{
    private readonly IWorkflowGraphValidator _graphValidator;
    private readonly IWorkflowNodeExecutorRegistry _executorRegistry;
    private readonly IWorkflowAuditRecorder _auditRecorder;
    private readonly IWorkflowRunStore? _runStore;
    private readonly IWorkflowNodeResourceHistoryRecorder? _resourceHistoryRecorder;

    public RankedWorkflowOrchestrator(
        IWorkflowGraphValidator graphValidator,
        IWorkflowNodeExecutorRegistry executorRegistry,
        IWorkflowAuditRecorder? auditRecorder = null,
        IWorkflowRunStore? runStore = null,
        IWorkflowNodeResourceHistoryRecorder? resourceHistoryRecorder = null)
    {
        _graphValidator = graphValidator;
        _executorRegistry = executorRegistry;
        _auditRecorder = auditRecorder ?? new InMemoryWorkflowAuditRecorder();
        _runStore = runStore;
        _resourceHistoryRecorder = resourceHistoryRecorder;
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
            targetNodeId);
        var orderedNodes = TopologicalSort(effectiveDefinition);
        var outputsByNodeId = new Dictionary<Guid, WorkflowNodeOutput>();

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

            workflowRun.Succeed(DateTimeOffset.UtcNow);
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
            // even when the caller's token is the reason the run aborted.
            await PersistRunAsync(workflowRun, CancellationToken.None);
            throw;
        }

        return new WorkflowRunResult(workflowRun, outputsByNodeId);
    }

    private Task PersistRunAsync(WorkflowRun workflowRun, CancellationToken cancellationToken)
        => _runStore is null
            ? Task.CompletedTask
            : _runStore.SaveAsync(workflowRun, cancellationToken);

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
