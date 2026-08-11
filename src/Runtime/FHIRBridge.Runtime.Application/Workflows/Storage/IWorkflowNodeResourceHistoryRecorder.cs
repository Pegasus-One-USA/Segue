namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Records what each node in a workflow run actually produced (fetched resources, transformed/mapped records,
/// destination write results), so the Execution History screen can show real fetch/transform/store detail per
/// run, not just per-node status. Implementations are expected to encrypt <c>PayloadJson</c> at rest, since it can
/// carry PHI (raw FHIR resources, mapped field values).
/// </summary>
public interface IWorkflowNodeResourceHistoryRecorder
{
    Task RecordNodeOutputAsync(
        Guid workflowRunId,
        Guid workflowNodeRunId,
        string nodeType,
        string contract,
        object? payload,
        CancellationToken cancellationToken);

    Task<WorkflowPagedResult<WorkflowNodeRunPayloadDto>> GetPagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Every node that actually started for this run — succeeded, failed, or cancelled — merged with
    /// whatever output payload it recorded (success only). Unlike <see cref="GetPagedAsync"/> (which only ever
    /// sees success-path writes, so a failed node is silently absent), this always has a row for a node that
    /// started, carrying its real <see cref="WorkflowNodeRunHistoryDto.Status"/>/<see
    /// cref="WorkflowNodeRunHistoryDto.ErrorMessage"/> either way.</summary>
    Task<WorkflowPagedResult<WorkflowNodeRunHistoryDto>> GetNodeRunHistoryPagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>One chain per (resource, destination field) touched by this run's transform-rule execution —
    /// see <see cref="FHIRBridge.Runtime.Domain.Workflows.FieldLineageEntry"/> for the underlying per-hop rows. Grouped
    /// server-side so a field's hop chain is never split across a page boundary.</summary>
    Task<WorkflowPagedResult<FieldLineageChainDto>> GetFieldLineagePagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed record WorkflowPagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record WorkflowNodeRunPayloadDto(
    Guid Id,
    Guid WorkflowNodeRunId,
    string NodeType,
    string Contract,
    string PayloadJson,
    int? ItemCount,
    DateTimeOffset RecordedAtUtc);

/// <summary>One node's full outcome for a run — status/error always present (sourced from the
/// <c>WorkflowNodeRun</c> row itself, which is written on every path: success, failure, cancellation), output
/// payload present only when the node actually produced one (success path, non-<c>None</c> contract).</summary>
public sealed record WorkflowNodeRunHistoryDto(
    Guid WorkflowNodeRunId,
    string NodeType,
    int Rank,
    int SubRank,
    string Status,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Contract,
    string? PayloadJson,
    int? ItemCount);

/// <summary>One node hop in a destination field's transform-rule chain — see
/// <see cref="FHIRBridge.Runtime.Domain.Workflows.FieldLineageEntry"/> for the persisted shape this projects from.</summary>
public sealed record FieldLineageHopDto(
    int NodeOrder,
    string NodeType,
    string ConfigJson,
    string? SourceValueJson,
    string? DestinationValueJson,
    bool Success,
    string? ErrorMessage,
    double? DurationMs);

/// <summary>A destination field's full source-to-destination lineage for one resource in this run, ordered by
/// <see cref="FieldLineageHopDto.NodeOrder"/>.</summary>
public sealed record FieldLineageChainDto(
    string ResourceType,
    string ResourceId,
    string DestinationField,
    string? SourceField,
    IReadOnlyList<FieldLineageHopDto> Hops);
