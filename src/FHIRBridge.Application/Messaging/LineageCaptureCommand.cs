namespace FHIRBridge.Application.Messaging;

/// <summary>
/// One node hop in a destination field's transform-rule chain, buffered in-memory by
/// <c>MappingNodeExecutor.ApplyTransformRulesAsync</c> during transform execution — no DB access happens while
/// this is built, so recording lineage never adds a synchronous write to the transform hot path.
/// </summary>
public sealed record LineageHopEntryDto(
    string DestinationField,
    string? SourceField,
    int NodeOrder,
    string NodeType,
    string ConfigJson,
    string? SourceValueJson,
    string? DestinationValueJson,
    bool Success,
    string? ErrorMessage,
    double? DurationMs);

/// <summary>
/// A batch of field-lineage hops for one resource, published to the messaging transport by the transform executor
/// and persisted out-of-band by <see cref="Abstractions.Messaging.ILineageCaptureCommandHandler"/> — the pipeline
/// thread that produced <see cref="Entries"/> never waits on the write. <see cref="MessageId"/> is the idempotency
/// key (see <see cref="Abstractions.Messaging.IProcessedMessageStore"/>).
/// </summary>
public sealed record LineageCaptureCommand(
    Guid WorkflowRunId,
    Guid WorkflowNodeId,
    string ResourceType,
    string ResourceId,
    IReadOnlyList<LineageHopEntryDto> Entries,
    string MessageId);
