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
    /// <summary>Sentinel <see cref="NodeType"/> for a field the Mapping node wrote verbatim — no transform
    /// rule chain ran for it at all. Recorded so every mapped field has a provenance row (lineage is "where did
    /// this value come from", not "what transformed it"), but it must be excluded anywhere the UI counts or
    /// labels actual transformation work — see <c>EfWorkflowNodeResourceHistoryRecorder.GetLineageSummaryAsync</c>.</summary>
    public const string PassThroughNodeType = "DirectMapping";

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
        DateTimeOffset executedAtUtc,
        DateTimeOffset recordedAtUtc,
        string? sourceSystemType = null,
        string? sourceConnectionName = null,
        string? destinationTypeName = null,
        string? destinationName = null)
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
        ExecutedAtUtc = executedAtUtc;
        RecordedAtUtc = recordedAtUtc;
        SourceSystemType = sourceSystemType;
        SourceConnectionName = sourceConnectionName;
        DestinationTypeName = destinationTypeName;
        DestinationName = destinationName;
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

    /// <summary>Encrypted at rest via <see cref="Application.Abstractions.Security.IPhiFieldEncryptor"/> — a
    /// hop's before/after value can carry raw PHI (birthdates, names, clinical values). Never read directly;
    /// always go through <c>LineageCaptureCommandHandler</c>/<c>EfWorkflowNodeResourceHistoryRecorder</c>.</summary>
    public string? SourceValueJson { get; }
    public string? DestinationValueJson { get; }
    public bool Success { get; }
    public string? ErrorMessage { get; }
    public double? DurationMs { get; }

    /// <summary>When this specific hop actually ran — distinct from <see cref="RecordedAtUtc"/> (when the
    /// whole resource's batch of hops was persisted).</summary>
    public DateTimeOffset ExecutedAtUtc { get; }
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>Resolved once per node execution (same for every hop/resource in the batch) — the source
    /// connection's vendor and configured display name.</summary>
    public string? SourceSystemType { get; }
    public string? SourceConnectionName { get; }

    /// <summary>Resolved once per node execution — the destination's type and configured display name.</summary>
    public string? DestinationTypeName { get; }
    public string? DestinationName { get; }
}
