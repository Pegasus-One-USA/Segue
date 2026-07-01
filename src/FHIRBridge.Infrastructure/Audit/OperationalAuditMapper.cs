using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Audit;

internal static class OperationalAuditMapper
{
    private const int ResourceTypeMaxLength = 100;
    private const int ActionMaxLength = 100;
    private const int StatusMaxLength = 50;
    private const int MessageMaxLength = 1000;
    private const int TriggeredByMaxLength = 200;
    private const int CorrelationIdMaxLength = 200;

    public static OperationalAuditLog ToEntity(RecordOperationalAuditLogRequest request)
    {
        return new OperationalAuditLog(
            request.TenantId,
            request.PipelineRunId,
            request.ResourcePipelineRouteId,
            request.SourceConnectionId,
            request.DestinationId,
            request.MappingProfileId,
            Truncate(request.ResourceType, ResourceTypeMaxLength),
            TruncateRequired(request.Action, ActionMaxLength),
            TruncateRequired(request.Status, StatusMaxLength),
            TruncateRequired(request.Message, MessageMaxLength),
            request.ResourceCount,
            Truncate(request.TriggeredBy, TriggeredByMaxLength),
            Truncate(request.CorrelationId, CorrelationIdMaxLength),
            DateTime.UtcNow);
    }

    public static OperationalAuditLogDto ToDto(OperationalAuditLog auditLog)
    {
        return new OperationalAuditLogDto(
            auditLog.Id,
            auditLog.TenantId,
            auditLog.PipelineRunId,
            auditLog.ResourcePipelineRouteId,
            auditLog.SourceConnectionId,
            auditLog.DestinationId,
            auditLog.MappingProfileId,
            auditLog.ResourceType,
            auditLog.Action,
            auditLog.Status,
            auditLog.Message,
            auditLog.ResourceCount,
            auditLog.TriggeredBy,
            auditLog.CorrelationId,
            auditLog.OccurredOnUtc);
    }

    private static string TruncateRequired(string value, int maxLength)
    {
        return Truncate(value, maxLength) ?? string.Empty;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        const string suffix = "...";
        return value[..(maxLength - suffix.Length)] + suffix;
    }
}
