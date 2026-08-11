namespace FHIRBridge.Runtime.Domain.Workflows;

/// <summary>
/// One node hop in a destination field's transform-rule chain for a single resource — the field-level
/// counterpart to <see cref="WorkflowNodeRun.LineageJson"/> (node/contract/metadata only) and
/// <see cref="WorkflowNodeRunPayload"/> (raw node output only). Written out-of-band by the Worker's
/// lineage-capture consumer from a <c>LineageCaptureCommand</c> the transform executor publishes — never
/// written synchronously from the pipeline's own execution path. Keyed by <see cref="WorkflowNodeId"/> (the
/// static <see cref="WorkflowNode"/> definition), not a per-run node-run id, since that id isn't assigned until
/// after the executor that produces these entries has already returned.
/// </summary>
public sealed class FieldLineageEntry
{
    public FieldLineageEntry(
        Guid id,
        Guid workflowRunId,
        Guid workflowNodeId,
        string resourceType,
        string resourceId,
        string destinationField,
        string? sourceField,
        int nodeOrder,
        string nodeType,
        string configJson,
        string? sourceValueJson,
        string? destinationValueJson,
        bool success,
        string? errorMessage,
        double? durationMs,
        DateTimeOffset recordedAtUtc)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowRunId = workflowRunId;
        WorkflowNodeId = workflowNodeId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        DestinationField = destinationField;
        SourceField = sourceField;
        NodeOrder = nodeOrder;
        NodeType = nodeType;
        ConfigJson = configJson;
        SourceValueJson = sourceValueJson;
        DestinationValueJson = destinationValueJson;
        Success = success;
        ErrorMessage = errorMessage;
        DurationMs = durationMs;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; }
    public Guid WorkflowRunId { get; }
    public Guid WorkflowNodeId { get; }
    public string ResourceType { get; }
    public string ResourceId { get; }
    public string DestinationField { get; }
    public string? SourceField { get; }
    public int NodeOrder { get; }
    public string NodeType { get; }
    public string ConfigJson { get; }
    public string? SourceValueJson { get; }
    public string? DestinationValueJson { get; }
    public bool Success { get; }
    public string? ErrorMessage { get; }
    public double? DurationMs { get; }
    public DateTimeOffset RecordedAtUtc { get; }
}
