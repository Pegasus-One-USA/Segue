namespace FHIRBridge.Application.DTOs;

/// <summary>Inputs for recording one user-activity audit row. The hash chain is computed by the service.</summary>
public sealed record RecordUserActivityRequest(
    Guid? TenantId,
    Guid? UserId,
    string UserEmail,
    string Category,
    string Activity,
    string Status,
    string? EntityName = null,
    Guid? EntityId = null,
    string? IpAddress = null,
    string? UserAgent = null,
    string? HttpMethod = null,
    string? RequestPath = null,
    string? Details = null,
    string? CorrelationId = null,
    string? SessionId = null,
    string? FailureReason = null,
    string Severity = UserActivitySeverities.Information);

/// <summary>Stable category buckets for <see cref="RecordUserActivityRequest.Category"/>.</summary>
public static class UserActivityCategories
{
    public const string Authentication = "Authentication";
    public const string Authorization = "Authorization";
    public const string Configuration = "Configuration";
    public const string DataAccess = "DataAccess";
    public const string Administration = "Administration";
}

/// <summary>Stable outcome values for <see cref="RecordUserActivityRequest.Status"/>.</summary>
public static class UserActivityStatuses
{
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Denied = "Denied";
}

/// <summary>Stable severity values for <see cref="RecordUserActivityRequest.Severity"/>.</summary>
public static class UserActivitySeverities
{
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
}
