using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

/// <summary>
/// Wraps a real node executor to append a PHI-free <see cref="ResourceLineageRecord"/> per resource once the node
/// succeeds, so a workflow-graph run produces the same chain-of-custody trail as the older Configured Pipeline path
/// (<c>ConfiguredPipelineService</c>). Source/Transform/Compliance nodes report on their own output; Destination
/// nodes have no per-resource output (just a write count), so they report on their inputs — the records that were
/// actually written. Analytics nodes are not part of the chain of custody and are left unwrapped.
/// </summary>
public sealed class LineageTrackingWorkflowNodeExecutor : IWorkflowNodeExecutor
{
    private readonly IWorkflowNodeExecutor _inner;
    private readonly ILineageTracker _lineageTracker;

    public LineageTrackingWorkflowNodeExecutor(IWorkflowNodeExecutor inner, ILineageTracker lineageTracker)
    {
        _inner = inner;
        _lineageTracker = lineageTracker;
    }

    public string NodeType => _inner.NodeType;

    public async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var output = await _inner.ExecuteAsync(context, node, inputs, cancellationToken);

        var action = MapAction(node.Category);
        if (action is not null)
        {
            await RecordLineageAsync(context, node, inputs, output, action, cancellationToken);
        }

        return output;
    }

    private async Task RecordLineageAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        WorkflowNodeOutput output,
        string action,
        CancellationToken cancellationToken)
    {
        // Destination nodes emit a DestinationWriteResult (aggregate count only) — the per-resource identity lives
        // on what fed the node, not what it produced. Every other tracked category carries resource identity forward
        // in its own output.
        var resources = node.Category == WorkflowNodeCategory.Destination
            ? PassThroughNodeExecutor.ReadMappedRecords(inputs)
                .Select(record => (record.ResourceType, record.SourceResourceId))
            : PassThroughNodeExecutor.ReadResourceEnvelopes([output])
                .Select(envelope => (envelope.ResourceType, (string?)envelope.ResourceId));

        foreach (var (resourceType, sourceResourceId) in resources)
        {
            await _lineageTracker.RecordAsync(
                new ResourceLineageRecord(
                    context.WorkflowRunId,
                    null,
                    null,
                    null,
                    null,
                    resourceType,
                    sourceResourceId,
                    action,
                    "Completed",
                    DateTime.UtcNow),
                cancellationToken);
        }
    }

    private static string? MapAction(WorkflowNodeCategory category) => category switch
    {
        WorkflowNodeCategory.Source => "ResourceAccessed",
        WorkflowNodeCategory.Transform => "ResourceNormalized",
        WorkflowNodeCategory.Compliance => "ResourceDeIdentified",
        WorkflowNodeCategory.Destination => "ResourceWritten",
        _ => null,
    };
}
