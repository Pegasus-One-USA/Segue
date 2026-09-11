using System.Linq.Expressions;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using FHIRBridge.Application.Governance;

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

    public async Task<PagedResult<AuditLogDto>> GetAuditLogsAsync(
        string? correlationId, string? entityType, string? entityId, int skip, int take, CancellationToken cancellationToken,
        string? sortColumn = null, string? sortDirection = null)
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

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await ApplyAuditLogSort(query, sortColumn, sortDirection)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new AuditLogDto(
                x.Id, x.OccurredOnUtc, x.Actor, x.Module, x.Action, x.EntityType, x.EntityId, x.EntityName,
                x.OldValueJson, x.NewValueJson, x.Status, x.IpAddress, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    // A null/unrecognized sortColumn always falls back to the original hardcoded ordering (newest-first
    // by the hash-chain's own sequence) regardless of sortDirection — version-history mode (see
    // AuditLogsComponent.historyMode/versionByEntryId on the frontend) depends on that exact default and
    // never sends a sortColumn itself, so this stays correct even if a stale sort param somehow arrives.
    private static IOrderedQueryable<AuditLog> ApplyAuditLogSort(
        IQueryable<AuditLog> query, string? sortColumn, string? sortDirection)
    {
        var descending = !string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);

        IOrderedQueryable<AuditLog> Order<TKey>(Expression<Func<AuditLog, TKey>> keySelector) =>
            descending ? query.OrderByDescending(keySelector) : query.OrderBy(keySelector);

        return sortColumn switch
        {
            "occurredOnUtc" => Order(x => x.OccurredOnUtc),
            "actor"         => Order(x => x.Actor),
            "module"        => Order(x => x.Module),
            "action"        => Order(x => x.Action),
            "entity"        => Order(x => x.EntityName ?? x.EntityId ?? string.Empty),
            "status"        => Order(x => x.Status),
            "correlationId" => Order(x => x.CorrelationId ?? string.Empty),
            _               => query.OrderByDescending(x => x.SequenceNumber),
        };
    }

    public async Task<PagedResult<DataAccessLogDto>> GetDataAccessLogsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.DataAccessLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new DataAccessLogDto(
                x.Id, x.OccurredOnUtc, x.Actor, x.ResourceType, x.ResourceId, x.Action,
                x.PatientId, x.Purpose, x.PipelineRunId, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<AuthenticationLogDto>> GetAuthenticationLogsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken, string? authenticationTypePrefix = null)
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

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new AuthenticationLogDto(
                x.Id, x.OccurredOnUtc, x.UserEmail, x.AuthenticationType, x.Success,
                x.FailureReason, x.IpAddress, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<SecurityEventDto>> GetSecurityEventsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.SecurityEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new SecurityEventDto(
                x.Id, x.OccurredOnUtc, x.Severity, x.EventType, x.UserEmail,
                x.IpAddress, x.Details, x.Resolved, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<AuthorizationLogDto>> GetAuthorizationLogsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.AuthorizationLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new AuthorizationLogDto(
                x.Id, x.OccurredOnUtc, x.UserEmail, x.RequestPath, x.PermissionCode, x.Result, x.IpAddress, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<SchedulerHistoryDto>> GetSchedulerHistoryAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.SchedulerHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.RunTimeUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new SchedulerHistoryDto(x.Id, x.SchedulerId, x.RunTimeUtc, x.Status, x.RouteCount, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<RetryHistoryDto>> GetRetryHistoryAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.RetryHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new RetryHistoryDto(
                x.Id, x.OccurredOnUtc, x.Context, x.RetryNumber, x.DelayMilliseconds, x.Reason, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<ErrorLogDto>> GetErrorLogsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ErrorLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new ErrorLogDto(
                x.Id, x.OccurredOnUtc, x.Severity, x.ExceptionType, x.Message, x.StackTrace, x.Module, x.CorrelationId,
                x.ErrorReferenceId, x.Category, x.UserFriendlyMessage, x.ExecutionId, x.WorkflowId, x.EndpointId,
                x.RequestId, x.TraceId, x.SpanId, (string?)null, (string?)null, (DateTime?)null,
                x.DiagnosisAction, x.DiagnosisCause))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<ErrorLogDto>> SearchErrorLogsAsync(
        ErrorLogSearch search, CancellationToken cancellationToken)
    {
        var query = _dbContext.ErrorLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search.ErrorReferenceId))
        {
            // Operations -> Errors is the one governance filter a support engineer hand-types (the rest arrive via
            // deep-link), and ErrorReference.New() always mints uppercase ("ERR-yyyyMMdd-" + uppercase base-36).
            // Normalizing the term instead of comparing case-insensitively in SQL keeps this matching on
            // PostgreSQL (whose = is case-sensitive, unlike SQL Server's default collation) while still using the
            // unique index on ErrorLogs.ErrorReferenceId.
            var errorReferenceId = search.ErrorReferenceId.Trim().ToUpperInvariant();
            query = query.Where(x => x.ErrorReferenceId == errorReferenceId);
        }
        if (!string.IsNullOrWhiteSpace(search.CorrelationId))
            query = query.Where(x => x.CorrelationId == search.CorrelationId);
        if (!string.IsNullOrWhiteSpace(search.ExecutionId))
            query = query.Where(x => x.ExecutionId == search.ExecutionId);
        if (!string.IsNullOrWhiteSpace(search.WorkflowId))
            query = query.Where(x => x.WorkflowId == search.WorkflowId);
        if (!string.IsNullOrWhiteSpace(search.EndpointId))
            query = query.Where(x => x.EndpointId == search.EndpointId);
        if (!string.IsNullOrWhiteSpace(search.Severity))
            query = query.Where(x => x.Severity == search.Severity);
        else
            // Informational rows (routine sub-500 rejections captured via CaptureExpectedAsync) are findable
            // by CorrelationId/ExecutionId/etc. but must stay out of the default Operations → Errors view —
            // that's the whole point of not routing them through the heavy 5xx CaptureAsync path.
            query = query.Where(x => x.Severity != "Informational");
        if (!string.IsNullOrWhiteSpace(search.Category))
            query = query.Where(x => x.Category == search.Category);
        if (search.FromUtc.HasValue)
            query = query.Where(x => x.OccurredOnUtc >= search.FromUtc.Value);
        if (search.ToUtc.HasValue)
            query = query.Where(x => x.OccurredOnUtc <= search.ToUtc.Value);

        // Left-join resolution triage state; absence of a row means "Open".
        var joined = from e in query
                     join r in _dbContext.ErrorResolutions.AsNoTracking()
                         on e.ErrorReferenceId equals r.ErrorReferenceId into resolutions
                     from r in resolutions.DefaultIfEmpty()
                     select new { Error = e, Resolution = r };

        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            joined = search.Status == ErrorResolution.StatusResolved
                ? joined.Where(x => x.Resolution != null && x.Resolution.Status == ErrorResolution.StatusResolved)
                : joined.Where(x => x.Resolution == null || x.Resolution.Status == ErrorResolution.StatusOpen);
        }

        var normalizedTake = NormalizeTake(search.Take);
        var totalCount = await joined.CountAsync(cancellationToken);
        var items = await joined
            .OrderByDescending(x => x.Error.OccurredOnUtc)
            .Skip(NormalizeSkip(search.Skip))
            .Take(normalizedTake)
            .Select(x => new ErrorLogDto(
                x.Error.Id, x.Error.OccurredOnUtc, x.Error.Severity, x.Error.ExceptionType, x.Error.Message,
                x.Error.StackTrace, x.Error.Module, x.Error.CorrelationId,
                x.Error.ErrorReferenceId, x.Error.Category, x.Error.UserFriendlyMessage, x.Error.ExecutionId,
                x.Error.WorkflowId, x.Error.EndpointId, x.Error.RequestId, x.Error.TraceId, x.Error.SpanId,
                x.Resolution == null ? ErrorResolution.StatusOpen : x.Resolution.Status,
                x.Resolution == null ? null : x.Resolution.ResolvedBy,
                x.Resolution == null ? null : x.Resolution.ResolvedOnUtc,
                x.Error.DiagnosisAction, x.Error.DiagnosisCause))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, search.Skip, normalizedTake);
    }

    public async Task<PagedResult<ApiRequestLogDto>> GetApiRequestLogsAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ApiRequestLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new ApiRequestLogDto(
                // Every argument spelled out: an EF expression tree cannot use a record's optional parameters.
                x.Id, x.OccurredOnUtc, x.Method, x.Url, x.StatusCode, x.DurationMs, x.Error, x.CorrelationId,
                x.Direction, ""))
            .ToListAsync(cancellationToken);

        // Described after materialization, not inside the projection: this is C# string matching that EF cannot
        // translate to SQL. Cheap enough at page size to be irrelevant, and it keeps the wording out of the
        // database entirely (see ApiRequestStepDescriber).
        var described = items
            .Select(item => item with { Step = ApiRequestStepDescriber.Describe(item.Method, item.Url, item.Direction) })
            .ToList();

        return ToPaged(described, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<ExportHistoryDto>> GetExportHistoryAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ExportHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new ExportHistoryDto(
                x.Id, x.OccurredOnUtc, x.PipelineRunId, x.DestinationName, x.Format, x.RowCount,
                x.FileSizeBytes, x.Status, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<NotificationHistoryDto>> GetNotificationHistoryAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.NotificationHistory.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new NotificationHistoryDto(
                x.Id, x.OccurredOnUtc, x.NotificationType, x.Recipient, x.Subject, x.Status, x.Error, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<ValidationFailureDto>> GetValidationFailuresAsync(
        string? correlationId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ValidationFailureLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new ValidationFailureDto(
                x.Id, x.OccurredOnUtc, x.ResourceType, x.ResourceId, x.WarningsJson,
                x.DataQualityScore, x.PipelineRunId, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<PagedResult<EndpointHealthCheckDto>> GetEndpointHealthChecksAsync(
        int skip, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.EndpointHealthChecks.AsNoTracking().AsQueryable();

        var normalizedTake = NormalizeTake(take);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip(NormalizeSkip(skip))
            .Take(normalizedTake)
            .Select(x => new EndpointHealthCheckDto(
                x.Id, x.OccurredOnUtc, x.EndpointName, x.EndpointType, x.Status, x.LatencyMs, x.Message))
            .ToListAsync(cancellationToken);

        return ToPaged(items, totalCount, skip, normalizedTake);
    }

    public async Task<IReadOnlyList<SmartLaunchLogDto>> GetSmartLaunchLogsAsync(
        int take, CancellationToken cancellationToken)
    {
        return await _dbContext.SmartLaunchLogs.AsNoTracking()
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(NormalizeTake(take))
            .Select(x => new SmartLaunchLogDto(
                x.Id, x.OccurredOnUtc, x.SourceConnectionId, x.SourceName, x.LaunchType, x.Success, x.FailureReason,
                x.GrantedScope, x.PatientContextGranted, x.TokenCacheKeyHash, x.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<CorrelationSearchResultDto> GetCorrelationSearchResultAsync(
        string correlationId, CancellationToken cancellationToken)
    {
        const int take = 500;

        var pipelineRun = await _pipelineRunRepository.GetByCorrelationIdAsync(correlationId, cancellationToken);
        var auditLogs = await GetAuditLogsAsync(correlationId, null, null, 0, take, cancellationToken);
        var dataAccessLogs = await GetDataAccessLogsAsync(correlationId, 0, take, cancellationToken);
        var authenticationLogs = await GetAuthenticationLogsAsync(correlationId, 0, take, cancellationToken);
        var securityEvents = await GetSecurityEventsAsync(correlationId, 0, take, cancellationToken);
        var authorizationLogs = await GetAuthorizationLogsAsync(correlationId, 0, take, cancellationToken);
        var schedulerHistory = await GetSchedulerHistoryAsync(correlationId, 0, take, cancellationToken);
        var retryHistory = await GetRetryHistoryAsync(correlationId, 0, take, cancellationToken);
        var errors = await GetErrorLogsAsync(correlationId, 0, take, cancellationToken);
        var apiRequests = await GetApiRequestLogsAsync(correlationId, 0, take, cancellationToken);
        var exports = await GetExportHistoryAsync(correlationId, 0, take, cancellationToken);
        var notifications = await GetNotificationHistoryAsync(correlationId, 0, take, cancellationToken);
        var validationFailures = await GetValidationFailuresAsync(correlationId, 0, take, cancellationToken);
        var workflowRuns = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(run => run.CorrelationId == correlationId)
            .OrderByDescending(run => run.StartedAt)
            .Take(take)
            .Select(run => new WorkflowRunSummaryDto(
                run.Id, run.WorkflowDefinitionId, run.Status.ToString(), run.StartedAt, run.CompletedAt,
                run.TriggeredBy, run.TriggerType, run.ErrorMessage))
            .ToListAsync(cancellationToken);
        var smartLaunchLogs = await _dbContext.SmartLaunchLogs
            .AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .OrderByDescending(x => x.OccurredOnUtc)
            .Take(take)
            .Select(x => new SmartLaunchLogDto(
                x.Id, x.OccurredOnUtc, x.SourceConnectionId, x.SourceName, x.LaunchType, x.Success, x.FailureReason,
                x.GrantedScope, x.PatientContextGranted, x.TokenCacheKeyHash, x.CorrelationId))
            .ToListAsync(cancellationToken);

        return new CorrelationSearchResultDto(
            correlationId, pipelineRun, auditLogs.Items, dataAccessLogs.Items, authenticationLogs.Items,
            securityEvents.Items, authorizationLogs.Items, schedulerHistory.Items, retryHistory.Items,
            errors.Items, apiRequests.Items, exports.Items, notifications.Items, validationFailures.Items,
            workflowRuns, smartLaunchLogs);
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
        var latestPerDataClass = await _dbContext.ArchiveManifestEntries.AsNoTracking()
            .GroupBy(x => x.DataClass)
            .Select(g => g.OrderByDescending(x => x.CreatedOnUtc).First())
            .ToListAsync(cancellationToken);

        return latestPerDataClass
            .OrderBy(x => x.DataClass)
            .Select(x => new ArchiveManifestDto(x.DataClass, x.ArchivedThroughUtc, x.FileLocation, x.RecordCount, x.CreatedOnUtc))
            .ToList();
    }

    private static PagedResult<T> ToPaged<T>(IReadOnlyList<T> items, int totalCount, int skip, int take) =>
        new(items, totalCount, (NormalizeSkip(skip) / take) + 1, take);

    private static int NormalizeTake(int take) => take is <= 0 or > 500 ? 200 : take;

    private static int NormalizeSkip(int skip) => skip < 0 ? 0 : skip;
}
