using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A durable, queryable field-level lineage fact: for one resource, one mapped field's source FHIR path resolved to
/// one destination column, optionally via a named transformation (terminology translation, normalization, ...).
/// This is what <see cref="ResourceLineageEntry"/> deliberately doesn't capture — resource-level lineage answers
/// "was this resource touched"; this answers "why does this destination column hold this value." Diagnostic, not
/// compliance-mandated, so purgeable like <see cref="ResourceLineageEntry"/> — and opt-in at the source (see
/// <c>FieldLineageOptions</c>) since the row volume is O(fields × resources), not O(resources).
/// </summary>
public sealed class FieldLineageEntry : Entity<Guid>
{
    private FieldLineageEntry()
    {
    }

    public FieldLineageEntry(
        Guid pipelineRunId,
        Guid? mappingProfileId,
        string resourceType,
        string? sourceResourceId,
        string sourceFieldPath,
        string transformationType,
        string? destinationObject,
        string destinationColumn,
        DateTime occurredOnUtc)
    {
        Id = Guid.NewGuid();
        PipelineRunId = pipelineRunId;
        MappingProfileId = mappingProfileId;
        ResourceType = resourceType;
        SourceResourceId = sourceResourceId;
        SourceFieldPath = sourceFieldPath;
        TransformationType = transformationType;
        DestinationObject = destinationObject;
        DestinationColumn = destinationColumn;
        OccurredOnUtc = occurredOnUtc;
    }

    public Guid PipelineRunId { get; private set; }
    public Guid? MappingProfileId { get; private set; }
    public string ResourceType { get; private set; } = default!;
    public string? SourceResourceId { get; private set; }
    public string SourceFieldPath { get; private set; } = default!;
    public string TransformationType { get; private set; } = default!;
    public string? DestinationObject { get; private set; }
    public string DestinationColumn { get; private set; } = default!;
    public DateTime OccurredOnUtc { get; private set; }
}
