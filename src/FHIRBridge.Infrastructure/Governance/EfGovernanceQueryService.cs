using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

public sealed class EfGovernanceQueryService : IGovernanceQueryService
{
    /// <summary>The four HIPAA audit tables that deliberately never implement IPurgeableStore — see GovernanceLogPurgeableStore's remarks.</summary>
    private static readonly string[] ImmutableDataClasses = ["AuditLog", "AuthenticationLog", "SmartLaunchLog", "DataAccessLog"];

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IConfiguredPipelineRunRepository _pipelineRunRepository;
    private readonly IReadOnlyList<IPurgeableStore> _purgeableStores;
    private readonly IRetentionPolicyService _retentionPolicyService;

    public EfGovernanceQueryService(
        FHIRBridgeDbContext dbContext,
        IConfiguredPipelineRunRepository pipelineRunRepository,
        IEnumerable<IPurgeableStore> purgeableStores,
        IRetentionPolicyService retentionPolicyService)
    {
        _dbContext = dbContext;
        _pipelineRunRepository = pipelineRunRepository;
        _purgeableStores = purgeableStores.ToList();
        _retentionPolicyService = retentionPolicyService;
    }

    public async Task<IReadOnlyList<AuditLogDto>> GetAuditLogsAsync(
        string? correlationId, string? entityType, string? entityId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(x => x.EntityType == entityType);
        }
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            query = query.Where(x => x.EntityId == entityId);
        }

        return await query
            .OrderByDescending(x => x.SequenceNumber)
            .Take(NormalizeTake(take))
            .Select(x => new AuditLogDto(
                x.Id, x.OccurredOnUtc, x.Actor, x.Module, x.Action, x.EntityType, x.EntityId, x.EntityName,
                x.OldValueJson, x.NewValueJson, x.Status, x.IpAddress, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DataAccessLogDto>> GetDataAccessLogsAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.DataAccessLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new DataAccessLogDto(
                x.Id, x.OccurredOnUtc, x.Actor, x.ResourceType, x.ResourceId, x.Action,
                x.PatientId, x.Purpose, x.PipelineRunId, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuthenticationLogDto>> GetAuthenticationLogsAsync(
        string? correlationId, int take, CancellationToken cancellationToken, string? authenticationTypePrefix = null)
    {
        var query = _dbContext.AuthenticationLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }
        if (!string.IsNullOrWhiteSpace(authenticationTypePrefix))
        {
            query = query.Where(x => x.AuthenticationType.StartsWith(authenticationTypePrefix));
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new AuthenticationLogDto(
                x.Id, x.OccurredOnUtc, x.UserEmail, x.AuthenticationType, x.Success,
                x.FailureReason, x.IpAddress, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SecurityEventDto>> GetSecurityEventsAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.SecurityEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new SecurityEventDto(
                x.Id, x.OccurredOnUtc, x.Severity, x.EventType, x.UserEmail,
                x.IpAddress, x.Details, x.Resolved, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuthorizationLogDto>> GetAuthorizationLogsAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.AuthorizationLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new AuthorizationLogDto(
                x.Id, x.OccurredOnUtc, x.UserEmail, x.RequestPath, x.PermissionCode, x.Result, x.IpAddress, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SchedulerHistoryDto>> GetSchedulerHistoryAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.SchedulerHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.RunTimeUtc)
            .Take(NormalizeTake(take))
            .Select(x => new SchedulerHistoryDto(x.Id, x.SchedulerId, x.RunTimeUtc, x.Status, x.RouteCount, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetryHistoryDto>> GetRetryHistoryAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.RetryHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new RetryHistoryDto(
                x.Id, x.OccurredOnUtc, x.Context, x.RetryNumber, x.DelayMilliseconds, x.Reason, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ErrorLogDto>> GetErrorLogsAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ErrorLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new ErrorLogDto(
                x.Id, x.OccurredOnUtc, x.Severity, x.ExceptionType, x.Message, x.StackTrace, x.Module, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApiRequestLogDto>> GetApiRequestLogsAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ApiRequestLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new ApiRequestLogDto(
                x.Id, x.OccurredOnUtc, x.Method, x.Url, x.StatusCode, x.DurationMs, x.Error, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExportHistoryDto>> GetExportHistoryAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ExportHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new ExportHistoryDto(
                x.Id, x.OccurredOnUtc, x.PipelineRunId, x.DestinationName, x.Format, x.RowCount,
                x.FileSizeBytes, x.Status, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationHistoryDto>> GetNotificationHistoryAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.NotificationHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new NotificationHistoryDto(
                x.Id, x.OccurredOnUtc, x.NotificationType, x.Recipient, x.Subject, x.Status, x.Error, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ValidationFailureDto>> GetValidationFailuresAsync(
        string? correlationId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ValidationFailureLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        return await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new ValidationFailureDto(
                x.Id, x.OccurredOnUtc, x.ResourceType, x.ResourceId, x.WarningsJson,
                x.DataQualityScore, x.PipelineRunId, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EndpointHealthCheckDto>> GetEndpointHealthChecksAsync(
        int take, CancellationToken cancellationToken)
    {
        return await _dbContext.EndpointHealthChecks.AsNoTracking()
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new EndpointHealthCheckDto(
                x.Id, x.OccurredOnUtc, x.EndpointName, x.EndpointType, x.Status, x.LatencyMs, x.Message))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartLaunchLogDto>> GetSmartLaunchLogsAsync(
        int take, CancellationToken cancellationToken)
    {
        return await _dbContext.SmartLaunchLogs.AsNoTracking()
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new SmartLaunchLogDto(
                x.Id, x.OccurredOnUtc, x.SourceConnectionId, x.SourceName, x.LaunchType, x.Success, x.FailureReason))
            .ToListAsync(cancellationToken);
    }

    public async Task<CorrelationSearchResultDto> GetCorrelationSearchResultAsync(
        string correlationId, CancellationToken cancellationToken)
    {
        const int take = 500;

        var pipelineRun = await _pipelineRunRepository.GetByCorrelationIdAsync(correlationId, cancellationToken);
        var auditLogs = await GetAuditLogsAsync(correlationId, null, null, take, cancellationToken);
        var dataAccessLogs = await GetDataAccessLogsAsync(correlationId, take, cancellationToken);
        var authenticationLogs = await GetAuthenticationLogsAsync(correlationId, take, cancellationToken);
        var securityEvents = await GetSecurityEventsAsync(correlationId, take, cancellationToken);
        var authorizationLogs = await GetAuthorizationLogsAsync(correlationId, take, cancellationToken);
        var schedulerHistory = await GetSchedulerHistoryAsync(correlationId, take, cancellationToken);
        var retryHistory = await GetRetryHistoryAsync(correlationId, take, cancellationToken);
        var errors = await GetErrorLogsAsync(correlationId, take, cancellationToken);
        var apiRequests = await GetApiRequestLogsAsync(correlationId, take, cancellationToken);
        var exports = await GetExportHistoryAsync(correlationId, take, cancellationToken);
        var notifications = await GetNotificationHistoryAsync(correlationId, take, cancellationToken);
        var validationFailures = await GetValidationFailuresAsync(correlationId, take, cancellationToken);

        return new CorrelationSearchResultDto(
            correlationId, pipelineRun, auditLogs, dataAccessLogs, authenticationLogs, securityEvents, authorizationLogs,
            schedulerHistory, retryHistory, errors, apiRequests, exports, notifications, validationFailures);
    }

    public Task<IReadOnlyList<RetentionPolicyDto>> GetRetentionPoliciesAsync(CancellationToken cancellationToken)
    {
        var policies = new List<RetentionPolicyDto>();

        foreach (var store in _purgeableStores)
        {
            var policy = _retentionPolicyService.GetPolicy(store.DataClass);
            policies.Add(new RetentionPolicyDto(store.DataClass, policy.RetentionYears, Purgeable: true));
        }

        foreach (var dataClass in ImmutableDataClasses)
        {
            var policy = _retentionPolicyService.GetPolicy(dataClass);
            policies.Add(new RetentionPolicyDto(dataClass, policy.RetentionYears, Purgeable: false));
        }

        return Task.FromResult<IReadOnlyList<RetentionPolicyDto>>(
            policies.OrderBy(x => x.DataClass, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<IReadOnlyList<ArchiveManifestDto>> GetArchiveManifestsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.ArchiveManifestEntries.AsNoTracking()
            .GroupBy(x => x.DataClass)
            .Select(g => g.OrderByDescending(x => x.CreatedOnUtc).First())
            .Select(x => new ArchiveManifestDto(x.DataClass, x.ArchivedThroughUtc, x.FileLocation, x.RecordCount, x.CreatedOnUtc))
            .OrderBy(x => x.DataClass)
            .ToListAsync(cancellationToken);
    }

    private static int NormalizeTake(int take) => take is <= 0 or > 500 ? 200 : take;
}
