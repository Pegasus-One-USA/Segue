namespace FHIRBridge.Application.Abstractions.Governance;

public interface ILineageTracker
{
    Task RecordAsync(
        ResourceLineageRecord record,
        CancellationToken cancellationToken);
}

public sealed record ResourceLineageRecord(
    Guid TenantId,
    Guid PipelineRunId,
    Guid? RouteId,
    Guid? SourceConnectionId,
    Guid? DestinationId,
    Guid? MappingProfileId,
    string ResourceType,
    string? SourceResourceId,
    string Action,
    string Status,
    DateTime OccurredOnUtc);
