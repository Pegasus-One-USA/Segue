namespace FHIRBridge.Application.DTOs;

public sealed record AlertRuleDto(
    Guid Id,
    string Name,
    string EventTypeFilter,
    int ThresholdCount,
    int WindowMinutes,
    string Severity,
    string Recipients,
    bool IsEnabled);

public sealed record CreateAlertRuleRequest(
    string Name,
    string EventTypeFilter,
    int ThresholdCount,
    int WindowMinutes,
    string Severity,
    string Recipients);

public sealed record AlertHistoryDto(
    Guid Id,
    Guid AlertRuleId,
    string RuleName,
    string Severity,
    string Summary,
    DateTime FiredOnUtc,
    bool Acknowledged,
    DateTime? AcknowledgedOnUtc,
    string? AcknowledgedBy);
