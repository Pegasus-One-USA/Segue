namespace FHIRBridge.Application.DTOs;

public sealed record UserActivityAuditLogDto(
    Guid Id,
    Guid? UserId,
    string UserEmail,
    string Category,
    string Activity,
    string Status,
    string? EntityName,
    Guid? EntityId,
    string? IpAddress,
    string? UserAgent,
    string? HttpMethod,
    string? RequestPath,
    string? Details,
    string? CorrelationId,
    string? SessionId,
    string? FailureReason,
    string Severity,
    DateTime OccurredOnUtc);
