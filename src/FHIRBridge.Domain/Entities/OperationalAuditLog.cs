using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class OperationalAuditLog : Entity<Guid>
{
    private OperationalAuditLog()
    {
    }

    public OperationalAuditLog(
        Guid? pipelineRunId,
        Guid? resourcePipelineRouteId,
        Guid? sourceConnectionId,
        Guid? destinationId,
        Guid? mappingProfileId,
        string? resourceType,
        string action,
        string status,
        string message,
        int? resourceCount,
        string? triggeredBy,
        string? correlationId,
        DateTime occurredOnUtc)
    {
        Id = Guid.NewGuid();
        PipelineRunId = pipelineRunId;
        ResourcePipelineRouteId = resourcePipelineRouteId;
        SourceConnectionId = sourceConnectionId;
        DestinationId = destinationId;
        MappingProfileId = mappingProfileId;
        ResourceType = resourceType;
        Action = action;
        Status = status;
        Message = message;
        ResourceCount = resourceCount;
        TriggeredBy = triggeredBy;
        CorrelationId = correlationId;
        OccurredOnUtc = occurredOnUtc;
    }

    public Guid? PipelineRunId { get; private set; }
    public Guid? ResourcePipelineRouteId { get; private set; }
    public Guid? SourceConnectionId { get; private set; }
    public Guid? DestinationId { get; private set; }
    public Guid? MappingProfileId { get; private set; }
    public string? ResourceType { get; private set; }
    public string Action { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public string Message { get; private set; } = default!;
    public int? ResourceCount { get; private set; }
    public string? TriggeredBy { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTime OccurredOnUtc { get; private set; }
}
