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
    /// server-side so a field's hop chain is never split across a page boundary. <paramref name="filter"/> backs
    /// the portal's Group-by-Field/Patient/Node views and free-text search — all the same underlying query, just
    /// filtered differently.</summary>
    Task<WorkflowPagedResult<FieldLineageChainDto>> GetFieldLineagePagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        FieldLineageFilter? filter,
        CancellationToken cancellationToken);

    /// <summary>Run-wide field-lineage totals (resources processed, fields transformed, distinct transform
    /// nodes executed, success rate) — backs the Lineage tab's stat strip.</summary>
    Task<LineageSummaryDto> GetLineageSummaryAsync(Guid workflowRunId, CancellationToken cancellationToken);

    /// <summary>Every resource type touched by this run's field lineage, each with the destination fields under
    /// it and how many distinct resources hit each one — backs the Lineage tab's resource-tree sidebar.</summary>
    Task<IReadOnlyList<ResourceTypeSummaryDto>> GetLineageResourceTreeAsync(Guid workflowRunId, CancellationToken cancellationToken);
}

/// <summary>Optional filters over <see cref="IWorkflowNodeResourceHistoryRecorder.GetFieldLineagePagedAsync"/> —
/// null/empty means "no filter" for that dimension. <see cref="Search"/> matches against destination field,
/// source field, node type, or resource id (substring, case-insensitive).</summary>
public sealed record FieldLineageFilter(
    string? ResourceType = null,
    string? DestinationField = null,
    string? ResourceId = null,
    string? NodeType = null,
    string? Search = null);

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
/// <see cref="FHIRBridge.Runtime.Domain.Workflows.FieldLineageEntry"/> for the persisted shape this projects from.
/// <see cref="SourceValueJson"/>/<see cref="DestinationValueJson"/> arrive here already decrypted.</summary>
public sealed record FieldLineageHopDto(
    int NodeOrder,
    string NodeType,
    string ConfigJson,
    string? SourceValueJson,
    string? DestinationValueJson,
    bool Success,
    string? ErrorMessage,
    double? DurationMs,
    DateTimeOffset ExecutedAtUtc);

/// <summary>A destination field's full source-to-destination lineage for one resource in this run, ordered by
/// <see cref="FieldLineageHopDto.NodeOrder"/>.</summary>
public sealed record FieldLineageChainDto(
    string ResourceType,
    string ResourceId,
    string DestinationField,
    string? SourceField,
    IReadOnlyList<FieldLineageHopDto> Hops,
    string? SourceSystemType,
    string? SourceConnectionName,
    string? DestinationTypeName,
    string? DestinationName);

/// <summary>Run-wide field-lineage totals — backs the Lineage tab's stat strip.</summary>
public sealed record LineageSummaryDto(
    int ResourcesProcessed,
    int FieldsTransformed,
    int TransformationNodesExecuted,
    double SuccessRate);

/// <summary>How many distinct resources hit one destination field, within one resource type — a leaf of
/// <see cref="ResourceTypeSummaryDto.Fields"/>.</summary>
public sealed record FieldSummaryDto(string DestinationField, int ResourceCount);

/// <summary>One resource type's field-lineage footprint for this run — backs the Lineage tab's resource-tree
/// sidebar (e.g. "Patient (8)" with "BirthDate (8)", "Gender (8)", ... nested under it).</summary>
public sealed record ResourceTypeSummaryDto(
    string ResourceType,
    int ResourceCount,
    IReadOnlyList<FieldSummaryDto> Fields);
