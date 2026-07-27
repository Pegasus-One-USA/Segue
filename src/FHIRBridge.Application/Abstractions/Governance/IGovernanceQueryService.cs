using FHIRBridge.Application.Abstractions.Persistence;
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
    Task<PagedResult<AuditLogDto>> GetAuditLogsAsync(
        string? correlationId, string? entityType, string? entityId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<DataAccessLogDto>> GetDataAccessLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<AuthenticationLogDto>> GetAuthenticationLogsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken, string? authenticationTypePrefix = null);

    Task<PagedResult<SecurityEventDto>> GetSecurityEventsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<AuthorizationLogDto>> GetAuthorizationLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<SchedulerHistoryDto>> GetSchedulerHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<RetryHistoryDto>> GetRetryHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<ErrorLogDto>> GetErrorLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Phase 6A – multi-criteria search for the Monitoring → Errors screen, including resolution status.</summary>
    Task<PagedResult<ErrorLogDto>> SearchErrorLogsAsync(ErrorLogSearch search, CancellationToken cancellationToken);

    Task<PagedResult<ApiRequestLogDto>> GetApiRequestLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<ExportHistoryDto>> GetExportHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<NotificationHistoryDto>> GetNotificationHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<ValidationFailureDto>> GetValidationFailuresAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken);

    Task<PagedResult<EndpointHealthCheckDto>> GetEndpointHealthChecksAsync(int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<SmartLaunchLogDto>> GetSmartLaunchLogsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Correlation Search — everything across every table that shares this CorrelationId, in one call.</summary>
    Task<CorrelationSearchResultDto> GetCorrelationSearchResultAsync(string correlationId, CancellationToken cancellationToken);

    /// <summary>The real, currently-effective retention policy for every governance/operations table (purgeable and immutable).</summary>
    Task<IReadOnlyList<RetentionPolicyDto>> GetRetentionPoliciesAsync(CancellationToken cancellationToken);

    /// <summary>The most recent archive-before-purge run for every data class that has ever been archived.</summary>
    Task<IReadOnlyList<ArchiveManifestDto>> GetArchiveManifestsAsync(CancellationToken cancellationToken);
}
