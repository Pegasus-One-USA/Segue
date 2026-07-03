using System.Text.Json;
using FHIRBridge.Runtime.Application.Workflows.Audit;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class RankedWorkflowOrchestrator : IRankedWorkflowOrchestrator
{
    private readonly IWorkflowGraphValidator _graphValidator;
    private readonly IWorkflowNodeExecutorRegistry _executorRegistry;
    private readonly IWorkflowAuditRecorder _auditRecorder;

    public RankedWorkflowOrchestrator(
        IWorkflowGraphValidator graphValidator,
        IWorkflowNodeExecutorRegistry executorRegistry,
        IWorkflowAuditRecorder? auditRecorder = null)
    {
        _graphValidator = graphValidator;
        _executorRegistry = executorRegistry;
        _auditRecorder = auditRecorder ?? new InMemoryWorkflowAuditRecorder();
    }

    public async Task<WorkflowRunResult> ExecuteAsync(
        WorkflowDefinition workflowDefinition,
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var validationResult = _graphValidator.Validate(workflowDefinition);
        if (!validationResult.IsValid)
        {
            throw new WorkflowGraphValidationException(validationResult.Errors);
        }

        var workflowRun = new WorkflowRun(context.WorkflowRunId, workflowDefinition.Id, DateTimeOffset.UtcNow);
        var orderedNodes = TopologicalSort(workflowDefinition);
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

                var incomingOutputs = GetIncomingOutputs(workflowDefinition, node, outputsByNodeId);
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
            throw;
        }

        return new WorkflowRunResult(workflowRun, outputsByNodeId);
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
