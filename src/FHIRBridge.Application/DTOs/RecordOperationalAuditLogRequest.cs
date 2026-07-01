namespace FHIRBridge.Application.DTOs;

public sealed record RecordOperationalAuditLogRequest(
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
    string? CorrelationId);
