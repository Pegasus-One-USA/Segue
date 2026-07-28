using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No-op query service for the in-memory (no-database) dev configuration — there is no store to read.</summary>
public sealed class EmptyGovernanceQueryService : IGovernanceQueryService
{
    public Task<PagedResult<AuditLogDto>> GetAuditLogsAsync(
        string? correlationId, string? entityType, string? entityId, int skip, int take, CancellationToken cancellationToken,
        string? sortColumn = null, string? sortDirection = null)
        => Task.FromResult(new PagedResult<AuditLogDto>([], 0, 1, take));

    public Task<PagedResult<DataAccessLogDto>> GetDataAccessLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<DataAccessLogDto>([], 0, 1, take));

    public Task<PagedResult<AuthenticationLogDto>> GetAuthenticationLogsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken, string? authenticationTypePrefix = null)
        => Task.FromResult(new PagedResult<AuthenticationLogDto>([], 0, 1, take));

    public Task<PagedResult<SecurityEventDto>> GetSecurityEventsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<SecurityEventDto>([], 0, 1, take));

    public Task<PagedResult<AuthorizationLogDto>> GetAuthorizationLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<AuthorizationLogDto>([], 0, 1, take));

    public Task<PagedResult<SchedulerHistoryDto>> GetSchedulerHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<SchedulerHistoryDto>([], 0, 1, take));

    public Task<PagedResult<RetryHistoryDto>> GetRetryHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<RetryHistoryDto>([], 0, 1, take));

    public Task<PagedResult<ErrorLogDto>> GetErrorLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<ErrorLogDto>([], 0, 1, take));

    public Task<PagedResult<ErrorLogDto>> SearchErrorLogsAsync(ErrorLogSearch search, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<ErrorLogDto>([], 0, 1, search.Take));

    public Task<PagedResult<ApiRequestLogDto>> GetApiRequestLogsAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<ApiRequestLogDto>([], 0, 1, take));

    public Task<PagedResult<ExportHistoryDto>> GetExportHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<ExportHistoryDto>([], 0, 1, take));

    public Task<PagedResult<NotificationHistoryDto>> GetNotificationHistoryAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<NotificationHistoryDto>([], 0, 1, take));

    public Task<PagedResult<ValidationFailureDto>> GetValidationFailuresAsync(string? correlationId, int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<ValidationFailureDto>([], 0, 1, take));

    public Task<PagedResult<EndpointHealthCheckDto>> GetEndpointHealthChecksAsync(int skip, int take, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<EndpointHealthCheckDto>([], 0, 1, take));

    public Task<IReadOnlyList<SmartLaunchLogDto>> GetSmartLaunchLogsAsync(int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SmartLaunchLogDto>>([]);

    public Task<CorrelationSearchResultDto> GetCorrelationSearchResultAsync(string correlationId, CancellationToken cancellationToken)
        => Task.FromResult(new CorrelationSearchResultDto(correlationId, null, [], [], [], [], [], [], [], [], [], [], [], []));

    public Task<IReadOnlyList<RetentionPolicyDto>> GetRetentionPoliciesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionPolicyDto>>([]);

    public Task<IReadOnlyList<ArchiveManifestDto>> GetArchiveManifestsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ArchiveManifestDto>>([]);
}
