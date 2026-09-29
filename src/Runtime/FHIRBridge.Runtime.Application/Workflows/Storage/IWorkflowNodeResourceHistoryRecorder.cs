namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Records what each node in a workflow run actually produced (fetched resources, transformed/mapped records,
/// destination write results), so the Execution History screen can show real fetch/transform/store detail per
/// run, not just per-node status. METADATA ONLY: implementations record how much each node produced (item and
/// per-resource-type counts) and destination delivery detail — never the resources themselves, which are PHI.
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

    /// <summary>The single node run's decrypted output payload — split out of <see cref="GetNodeRunHistoryPagedAsync"/>
    /// (which never decrypts) so the Execution History screen only pays the decryption cost for a node the user
    /// actually expands, rather than every node in the page up front. Null when the node run has no recorded
    /// payload (still running, failed before producing output, or a None-contract node).</summary>
    Task<WorkflowNodeRunPayloadDetailDto?> GetNodeRunPayloadAsync(
        Guid workflowRunId,
        Guid workflowNodeRunId,
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

    /// <summary>Per-node field-lineage breakdown for one run, keyed by workflow node id — what each node
    /// applied, rather than the run-wide totals <see cref="GetLineageSummaryAsync"/> returns. Nodes that
    /// recorded no field lineage are simply absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, NodeLineageBreakdownDto>> GetNodeLineageBreakdownAsync(
        Guid workflowRunId, CancellationToken cancellationToken);

    /// <summary>The transformation rules CONFIGURED on the workflow this run belongs to, grouped by resource
    /// type. Distinct from <see cref="GetNodeLineageBreakdownAsync"/>, which reports what actually executed.</summary>
    Task<IReadOnlyList<ConfiguredResourceTypeRulesDto>> GetConfiguredResourceTypeRulesAsync(
        Guid workflowRunId, CancellationToken cancellationToken);

    /// <summary>The DE-IDENTIFICATION rules configured for this run, grouped by resource type — the same
    /// shape as <see cref="GetConfiguredResourceTypeRulesAsync"/>, read from the de-identification profile
    /// rather than from the workflow's own transformation rules.</summary>
    Task<IReadOnlyList<ConfiguredResourceTypeRulesDto>> GetConfiguredDeIdentificationRulesAsync(
        Guid workflowRunId, CancellationToken cancellationToken);
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
    int? ItemCount,
    string? DeliveryDetailJson,
    /// <summary>Per-resource-type counts as JSON, e.g. {"Patient":1,"Observation":42}. Type names and totals
    /// only — the resources themselves are never stored. Null for non-resource-batch contracts.</summary>
    string? ResourceTypeCountsJson,
    DateTimeOffset RecordedAtUtc);

/// <summary>One node's full outcome for a run — status/error always present (sourced from the
/// <c>WorkflowNodeRun</c> row itself, which is written on every path: success, failure, cancellation), plus the
/// counts the node produced when it produced any. Counts only — node output is not retained.</summary>
public sealed record WorkflowNodeRunHistoryDto(
    Guid WorkflowNodeRunId,
    /// <summary>The DEFINITION node this run executed, as distinct from <see cref="WorkflowNodeRunId"/>
    /// (which identifies this one execution of it). Field lineage is recorded against the definition node,
    /// so this is what joins a row in the node list to what that node actually applied.</summary>
    Guid WorkflowNodeId,
    string NodeType,
    int Rank,
    int SubRank,
    string Status,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Contract,
    int? ItemCount,
    string? ResourceTypeCountsJson,
    string? DeliveryDetailJson);

/// <summary>One node run's output SUMMARY, fetched on demand when its row is expanded — counts only; the
/// output itself is not retained. See
/// <see cref="IWorkflowNodeResourceHistoryRecorder.GetNodeRunPayloadAsync"/>.</summary>
public sealed record WorkflowNodeRunPayloadDetailDto(
    Guid WorkflowNodeRunId,
    string? Contract,
    int? ItemCount,
    string? ResourceTypeCountsJson,
    string? DeliveryDetailJson);

/// <summary>One node hop in a destination field's transform-rule chain — see
/// <see cref="FHIRBridge.Runtime.Domain.Workflows.FieldLineageEntry"/> for the persisted shape this projects from.
/// Describes the transformation (node, config, outcome, timing); the field's before/after values are not
/// captured, since those are raw patient data.</summary>
public sealed record FieldLineageHopDto(
    int NodeOrder,
    string NodeType,
    string ConfigJson,
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

/// <summary>One resource type's CONFIGURED transformation rules — what the workflow is set up to do, as
/// opposed to what a given run happened to execute. Sourced from TransformationRules rather than from field
/// lineage, which matters in both directions: a rule defined but never triggered (no matching source value
/// in this run's data) still appears here, and the rules shown are genuinely the workflow's own configuration
/// rather than another node's runtime record.</summary>
public sealed record ConfiguredRuleCountDto(string NodeType, int RulesDefined);

/// <summary>The transformation rules configured for one resource type on a workflow.</summary>
public sealed record ConfiguredResourceTypeRulesDto(
    string ResourceType,
    int DistinctRuleTypes,
    IReadOnlyList<ConfiguredRuleCountDto> Rules);

/// <summary>Run-wide field-lineage totals — backs the Lineage tab's stat strip.</summary>
public sealed record LineageSummaryDto(
    int ResourcesProcessed,
    int FieldsTransformed,
    int TransformationNodesExecuted,
    double SuccessRate);

/// <summary>One rule type applied by a node, and how many times — e.g. ("DirectMapping", 2393) or
/// ("HumanNameParsing", 1). <see cref="NodeLineageBreakdownDto.Rules"/>' elements.</summary>
public sealed record LineageRuleCountDto(string NodeType, int Applications, int FailedApplications);

/// <summary>One resource type's share of a node's work — how many mappings it applied to that type, over how
/// many resources and distinct fields. The node list previously showed a single run-wide "mappings applied"
/// figure next to a separate per-resource-type item count, leaving the obvious question ("how many of those
/// mappings were Observations?") unanswered; this carries the split.</summary>
public sealed record LineageResourceTypeCountDto(
    string ResourceType,
    int Mappings,
    int Resources,
    int Fields,
    /// <summary>The transformation rules applied to THIS resource type, most-applied first. Frequently empty:
    /// rules are configured per resource type, so a type that is only ever copied field-for-field has none,
    /// and showing it an empty list is the accurate answer rather than a gap.</summary>
    IReadOnlyList<LineageRuleCountDto> Rules);

/// <summary>What one node actually did to the data, derived from the field-lineage this run recorded against
/// it. Execution History showed every node the same per-resource-type counts, which for a mapping node hid
/// the only thing worth knowing about it: how many field mappings and transformation rules it applied.
/// Empty <see cref="Rules"/> simply means the node records no field-level work (a source node fetches, a
/// normalization node reshapes whole resources) — its resource counts remain the honest summary.</summary>
public sealed record NodeLineageBreakdownDto(
    Guid WorkflowNodeId,
    int TotalApplications,
    int DistinctFields,
    int DistinctResources,
    IReadOnlyList<LineageRuleCountDto> Rules,
    IReadOnlyList<LineageResourceTypeCountDto> ResourceTypes);

/// <summary>How many distinct resources hit one destination field, within one resource type — a leaf of
/// <see cref="ResourceTypeSummaryDto.Fields"/>.</summary>
public sealed record FieldSummaryDto(string DestinationField, int ResourceCount);

/// <summary>One resource type's field-lineage footprint for this run — backs the Lineage tab's resource-tree
/// sidebar (e.g. "Patient (8)" with "BirthDate (8)", "Gender (8)", ... nested under it).</summary>
public sealed record ResourceTypeSummaryDto(
    string ResourceType,
    int ResourceCount,
    IReadOnlyList<FieldSummaryDto> Fields);
