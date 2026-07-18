using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>Read-only access to the governance log tables, backing the portal's Governance/Security screens.</summary>
public interface IGovernanceQueryService
{
    /// <summary>
    /// <paramref name="entityType"/>/<paramref name="entityId"/> scope this to one entity's version history —
    /// each returned row's <c>OldValueJson</c>/<c>NewValueJson</c> is itself that version's diff, so no separate
    /// "configuration comparison" data path is needed.
    /// </summary>
    Task<IReadOnlyList<AuditLogDto>> GetAuditLogsAsync(
        string? correlationId, string? entityType, string? entityId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<DataAccessLogDto>> GetDataAccessLogsAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthenticationLogDto>> GetAuthenticationLogsAsync(
        string? correlationId, int take, CancellationToken cancellationToken, string? authenticationTypePrefix = null);

    Task<IReadOnlyList<SecurityEventDto>> GetSecurityEventsAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthorizationLogDto>> GetAuthorizationLogsAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<SchedulerHistoryDto>> GetSchedulerHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetryHistoryDto>> GetRetryHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ErrorLogDto>> GetErrorLogsAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiRequestLogDto>> GetApiRequestLogsAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportHistoryDto>> GetExportHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<NotificationHistoryDto>> GetNotificationHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidationFailureDto>> GetValidationFailuresAsync(string? correlationId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<EndpointHealthCheckDto>> GetEndpointHealthChecksAsync(int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<SmartLaunchLogDto>> GetSmartLaunchLogsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Correlation Search — everything across every table that shares this CorrelationId, in one call.</summary>
    Task<CorrelationSearchResultDto> GetCorrelationSearchResultAsync(string correlationId, CancellationToken cancellationToken);

    /// <summary>The real, currently-effective retention policy for every governance/operations table (purgeable and immutable).</summary>
    Task<IReadOnlyList<RetentionPolicyDto>> GetRetentionPoliciesAsync(CancellationToken cancellationToken);

    /// <summary>The most recent archive-before-purge run for every data class that has ever been archived.</summary>
    Task<IReadOnlyList<ArchiveManifestDto>> GetArchiveManifestsAsync(CancellationToken cancellationToken);
}
