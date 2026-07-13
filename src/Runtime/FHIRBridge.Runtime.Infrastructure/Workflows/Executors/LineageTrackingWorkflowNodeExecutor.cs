using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

/// <summary>
/// Wraps a real node executor for two orthogonal governance concerns, both only observable from outside the
/// executor once it has run (or failed) — sharing one wrap point avoids double-wrapping every one of the ~45 node
/// executors:
/// <list type="bullet">
/// <item>Lineage: appends a PHI-free <see cref="ResourceLineageRecord"/> per resource once the node succeeds, so a
/// workflow-graph run produces the same chain-of-custody trail as the older Configured Pipeline path
/// (<c>ConfiguredPipelineService</c>). Source/Transform/Compliance nodes report on their own output; Destination
/// nodes have no per-resource output (just a write count), so they report on their inputs — the records that were
/// actually written. Analytics nodes are not part of the chain of custody and are left unwrapped.</item>
/// <item>Operational narration: a node-level Information entry on success ("Fetched N Patient" / "Wrote N row(s)")
/// and an Error entry if the node throws — the coarse, per-node counterpart to the step-level retry logging inside
/// <c>SourceNodeExecutor</c> itself. Compliance/Analytics nodes are not narrated; de-identification is already
/// covered by the lineage entry above, and Analytics isn't part of either governance concern.</item>
/// </list>
/// </summary>
public sealed class LineageTrackingWorkflowNodeExecutor : IWorkflowNodeExecutor
{
    private readonly IWorkflowNodeExecutor _inner;
    private readonly ILineageTracker _lineageTracker;
    private readonly IOperationalAuditService? _auditService;

    public LineageTrackingWorkflowNodeExecutor(
        IWorkflowNodeExecutor inner,
        ILineageTracker lineageTracker,
        IOperationalAuditService? auditService = null)
    {
        _inner = inner;
        _lineageTracker = lineageTracker;
        _auditService = auditService;
    }

    public string NodeType => _inner.NodeType;

    public async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        WorkflowNodeOutput output;
        try
        {
            output = await _inner.ExecuteAsync(context, node, inputs, cancellationToken);
        }
        catch (Exception exception)
        {
            await RecordNarrationAsync(
                context, node, OperationalLogSeverities.Error, "NodeExecutionFailed", "Failed",
                $"{node.Category} node '{node.NodeType}' failed: {exception.Message}", cancellationToken);
            throw;
        }

        var action = MapAction(node.Category);
        if (action is not null)
        {
            await RecordLineageAsync(context, node, inputs, output, action, cancellationToken);
        }

        await RecordNodeCompletionNarrationAsync(context, node, inputs, output, cancellationToken);

        return output;
    }

    // Node-level "something happened" narration for the Operational Log — the coarse counterpart to the step-level
    // retry entries SourceNodeExecutor logs internally. Only Source/Transform/Destination are narrated: Compliance's
    // de-identification is already covered by the lineage entry above, and Analytics isn't part of either concern.
    private Task RecordNodeCompletionNarrationAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        WorkflowNodeOutput output,
        CancellationToken cancellationToken)
    {
        var message = node.Category switch
        {
            WorkflowNodeCategory.Source => $"Fetched {CountRecords(output)} resource(s).",
            WorkflowNodeCategory.Transform => $"Processed {CountRecords(output)} record(s) in {node.NodeType}.",
            WorkflowNodeCategory.Destination when output.Payload is DestinationWriteResult write =>
                $"Wrote {write.RecordsWritten} row(s) to destination.",
            _ => null,
        };

        return message is null
            ? Task.CompletedTask
            : RecordNarrationAsync(context, node, OperationalLogSeverities.Information, "NodeExecutionCompleted", "Completed", message, cancellationToken);
    }

    private Task RecordNarrationAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        string severity,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        if (_auditService is null)
        {
            return Task.CompletedTask;
        }

        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                context.WorkflowRunId,
                null,
                null,
                null,
                null,
                null,
                action,
                status,
                message,
                null,
                null,
                context.CorrelationId,
                severity),
            cancellationToken);
    }

    // Tries both shapes a node's output can carry record identity in — ResourceEnvelope-based (Source/Compliance/
    // most Transform nodes) or MappedDestinationRecord-based (the Mapping node specifically outputs
    // MappedRecordBatch, which ReadResourceEnvelopes doesn't understand).
    private static int CountRecords(WorkflowNodeOutput output)
    {
        var resourceCount = PassThroughNodeExecutor.ReadResourceEnvelopes([output]).Count;
        return resourceCount > 0 ? resourceCount : PassThroughNodeExecutor.ReadMappedRecords([output]).Count;
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
