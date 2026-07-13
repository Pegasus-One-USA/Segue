namespace FHIRBridge.Application.DTOs;

public sealed record RecordOperationalAuditLogRequest(
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
    // Trailing optional so every pre-existing call site (config changes, connection tests, pipeline
    // start/complete, ...) keeps defaulting to Information without needing to change.
    string Severity = OperationalLogSeverities.Information);

/// <summary>Stable severity values for <see cref="RecordOperationalAuditLogRequest.Severity"/>.</summary>
public static class OperationalLogSeverities
{
    public const string Debug = "Debug";
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
}
