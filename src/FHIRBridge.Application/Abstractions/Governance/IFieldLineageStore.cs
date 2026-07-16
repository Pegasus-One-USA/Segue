namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Field-level lineage: for one resource, one mapped field's source FHIR path resolved to one destination column
/// (optionally via a named transformation). Unlike <see cref="ILineageStore"/>'s resource-level chain of custody
/// (which answers "was this resource touched"), this answers "why does this destination column hold this value" —
/// the FHIRBridge equivalent of column-level lineage. No dual-write echo to the operational audit log the way
/// <see cref="ILineageTracker"/> does for resource-level lineage: this is diagnostic, opt-in, and already scoped
/// narrowly enough (one mapping node, one resource) that a redacted echo elsewhere wouldn't add anything.
/// </summary>
public interface IFieldLineageStore
{
    Task AppendAsync(FieldLineageRecord record, CancellationToken cancellationToken);
}

public sealed record FieldLineageRecord(
    Guid PipelineRunId,
    Guid? MappingProfileId,
    string ResourceType,
    string? SourceResourceId,
    string SourceFieldPath,
    string TransformationType,
    string? DestinationObject,
    string DestinationColumn,
    DateTime OccurredOnUtc);

/// <summary>Always scoped to one resource — field lineage is a drill-down from an already-known resource-level
/// lineage step, not a standalone browsable list.</summary>
public sealed record FieldLineageQuery(
    string ResourceType,
    string SourceResourceId,
    Guid? PipelineRunId = null);

public interface IFieldLineageQueryService
{
    Task<IReadOnlyList<FieldLineageRecord>> GetFieldsAsync(FieldLineageQuery query, CancellationToken cancellationToken);
}
