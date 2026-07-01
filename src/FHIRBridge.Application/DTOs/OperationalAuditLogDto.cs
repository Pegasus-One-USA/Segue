namespace FHIRBridge.Application.DTOs;

public sealed record OperationalAuditLogDto(
    Guid Id,
    Guid TenantId,
    Guid? PipelineRunId,
    Guid? ResourcePipelineRouteId,
    Guid? SourceConnectionId,
    Guid? DestinationId,
    Guid? MappingProfileId,
    string? ResourceType,
    string Action,
    string Status,
    string Message,
    int? ResourceCount,
    string? TriggeredBy,
    string? CorrelationId,
    DateTime OccurredOnUtc);
