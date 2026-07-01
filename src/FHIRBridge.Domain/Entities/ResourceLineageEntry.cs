using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A durable, queryable lineage event capturing one step in a resource's chain of custody
/// (access → normalize → de-id → output). Unlike the immutable operational audit log, lineage entries
/// are purgeable under the retention policy. Holds only PHI-free metadata (resource ids/types, never content).
/// </summary>
public sealed class ResourceLineageEntry : Entity<Guid>
{
    private ResourceLineageEntry()
    {
    }

    public ResourceLineageEntry(
        Guid tenantId,
        Guid pipelineRunId,
        Guid? routeId,
        Guid? sourceConnectionId,
        Guid? destinationId,
        Guid? mappingProfileId,
        string resourceType,
        string? sourceResourceId,
        string action,
        string status,
        DateTime occurredOnUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        PipelineRunId = pipelineRunId;
        RouteId = routeId;
        SourceConnectionId = sourceConnectionId;
        DestinationId = destinationId;
        MappingProfileId = mappingProfileId;
        ResourceType = resourceType;
        SourceResourceId = sourceResourceId;
        Action = action;
        Status = status;
        OccurredOnUtc = occurredOnUtc;
    }

    public Guid TenantId { get; private set; }
    public Guid PipelineRunId { get; private set; }
    public Guid? RouteId { get; private set; }
    public Guid? SourceConnectionId { get; private set; }
    public Guid? DestinationId { get; private set; }
    public Guid? MappingProfileId { get; private set; }
    public string ResourceType { get; private set; } = default!;
    public string? SourceResourceId { get; private set; }
    public string Action { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public DateTime OccurredOnUtc { get; private set; }
}
