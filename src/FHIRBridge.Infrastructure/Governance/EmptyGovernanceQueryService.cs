using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No-op query service for the in-memory (no-database) dev configuration — there is no store to read.</summary>
public sealed class EmptyGovernanceQueryService : IGovernanceQueryService
{
    public Task<IReadOnlyList<AuditLogDto>> GetAuditLogsAsync(
        string? correlationId, string? entityType, string? entityId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);

    public Task<IReadOnlyList<DataAccessLogDto>> GetDataAccessLogsAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DataAccessLogDto>>([]);

    public Task<IReadOnlyList<AuthenticationLogDto>> GetAuthenticationLogsAsync(
        string? correlationId, int take, CancellationToken cancellationToken, string? authenticationTypePrefix = null)
        => Task.FromResult<IReadOnlyList<AuthenticationLogDto>>([]);

    public Task<IReadOnlyList<SecurityEventDto>> GetSecurityEventsAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SecurityEventDto>>([]);

    public Task<IReadOnlyList<AuthorizationLogDto>> GetAuthorizationLogsAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AuthorizationLogDto>>([]);

    public Task<IReadOnlyList<SchedulerHistoryDto>> GetSchedulerHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SchedulerHistoryDto>>([]);

    public Task<IReadOnlyList<RetryHistoryDto>> GetRetryHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetryHistoryDto>>([]);

    public Task<IReadOnlyList<ErrorLogDto>> GetErrorLogsAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ErrorLogDto>>([]);

    public Task<IReadOnlyList<ApiRequestLogDto>> GetApiRequestLogsAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiRequestLogDto>>([]);

    public Task<IReadOnlyList<ExportHistoryDto>> GetExportHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ExportHistoryDto>>([]);

    public Task<IReadOnlyList<NotificationHistoryDto>> GetNotificationHistoryAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<NotificationHistoryDto>>([]);

    public Task<IReadOnlyList<ValidationFailureDto>> GetValidationFailuresAsync(string? correlationId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ValidationFailureDto>>([]);

    public Task<IReadOnlyList<EndpointHealthCheckDto>> GetEndpointHealthChecksAsync(int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EndpointHealthCheckDto>>([]);

    public Task<IReadOnlyList<SmartLaunchLogDto>> GetSmartLaunchLogsAsync(int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SmartLaunchLogDto>>([]);

    public Task<CorrelationSearchResultDto> GetCorrelationSearchResultAsync(string correlationId, CancellationToken cancellationToken)
        => Task.FromResult(new CorrelationSearchResultDto(correlationId, null, [], [], [], [], [], [], [], [], [], [], [], []));

    public Task<IReadOnlyList<RetentionPolicyDto>> GetRetentionPoliciesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionPolicyDto>>([]);

    public Task<IReadOnlyList<ArchiveManifestDto>> GetArchiveManifestsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ArchiveManifestDto>>([]);
}
