using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// EF-backed <see cref="IGovernanceLogger"/>. Deliberately independent of
/// <see cref="AuditingSaveChangesInterceptor"/> (which handles config-entity changes automatically) —
/// this covers events that don't correspond to a tracked entity change: login/logout, resource access
/// decisions, and ad hoc security events. Each call is its own SaveChanges, since callers are typically
/// outside of an already-in-flight unit of work.
/// </summary>
public sealed class EfGovernanceLogger : IGovernanceLogger
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ICurrentUserService _currentUserService;

    public EfGovernanceLogger(FHIRBridgeDbContext dbContext, ICurrentUserService currentUserService)
    {
        _dbContext = dbContext;
        _currentUserService = currentUserService;
    }

    public async Task LogAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        var previousHash = await _dbContext.AuditLogs
            .OrderByDescending(x => x.SequenceNumber)
            .Select(x => x.EntryHash)
            .FirstOrDefaultAsync(cancellationToken);

        await PersistAsync(new AuditLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            current.AuditName,
            entry.Module,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.EntityName,
            entry.OldValueJson,
            entry.NewValueJson,
            entry.Status,
            entry.Remarks,
            current.IpAddress,
            Truncate(current.UserAgent, 500),
            entry.CorrelationId ?? current.CorrelationId,
            previousHash), cancellationToken);
    }

    public async Task LogDataAccessAsync(DataAccessEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        await PersistAsync(new DataAccessLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            current.AuditName,
            entry.ResourceType,
            entry.ResourceId,
            entry.Action,
            entry.PatientId,
            entry.Purpose,
            entry.PipelineRunId,
            entry.CorrelationId ?? current.CorrelationId,
            current.IpAddress), cancellationToken);
    }

    public async Task LogAuthenticationAsync(AuthenticationEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        await PersistAsync(new AuthenticationLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.UserEmail ?? current.Email,
            entry.AuthenticationType,
            entry.Success,
            Truncate(entry.FailureReason, 500),
            current.IpAddress,
            Truncate(current.UserAgent, 500),
            entry.CorrelationId ?? current.CorrelationId), cancellationToken);
    }

    public async Task LogSecurityEventAsync(SecurityEventEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        await PersistAsync(new SecurityEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.Severity,
            entry.EventType,
            entry.UserEmail ?? current.Email,
            current.IpAddress,
            entry.Details,
            entry.CorrelationId ?? current.CorrelationId), cancellationToken);
    }

    public async Task LogAuthorizationAsync(AuthorizationEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        await PersistAsync(new AuthorizationLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.UserEmail ?? current.Email,
            Truncate(entry.RequestPath, 500)!,
            entry.PermissionCode,
            entry.Result,
            current.IpAddress,
            entry.CorrelationId ?? current.CorrelationId), cancellationToken);
    }

    public async Task LogSchedulerRunAsync(SchedulerRunEntry entry, CancellationToken cancellationToken = default)
    {
        await PersistAsync(new SchedulerHistory(
            Guid.NewGuid(),
            entry.SchedulerId,
            DateTime.UtcNow,
            entry.Status,
            entry.RouteCount,
            entry.CorrelationId), cancellationToken);
    }

    public async Task LogRetryAsync(RetryEntry entry, CancellationToken cancellationToken = default)
    {
        await PersistAsync(new RetryHistory(
            Guid.NewGuid(),
            DateTime.UtcNow,
            Truncate(entry.Context, 500)!,
            entry.RetryNumber,
            entry.DelayMilliseconds,
            entry.Reason,
            entry.CorrelationId), cancellationToken);
    }

    public async Task LogErrorAsync(ErrorEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        var errorLog = new ErrorLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.Severity,
            entry.ExceptionType,
            entry.Message,
            entry.StackTrace,
            entry.Module,
            entry.CorrelationId ?? current.CorrelationId,
            entry.ErrorReferenceId,
            entry.Category,
            entry.UserFriendlyMessage,
            entry.ExecutionId,
            entry.WorkflowId,
            entry.EndpointId,
            entry.RequestId,
            entry.TraceId,
            entry.SpanId,
            entry.DiagnosisAction?.ToString(),
            Truncate(entry.DiagnosisCause, 500));

        _dbContext.ErrorLogs.Add(errorLog);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A failed insert (e.g. a reference-id collision — see ErrorReference's remarks) leaves this entity
            // tracked as Added; without detaching it here, GlobalExceptionManager's retry-with-a-fresh-id would
            // resubmit this same doomed-to-fail entity alongside the new one on every subsequent SaveChangesAsync
            // in this scope, guaranteeing every retry fails too regardless of whether the new id would have worked.
            _dbContext.Entry(errorLog).State = EntityState.Detached;
            throw;
        }
    }

    public async Task LogApiRequestAsync(ApiRequestEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        await PersistAsync(new ApiRequestLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.Method,
            Truncate(entry.Url, 1000)!,
            entry.StatusCode,
            entry.DurationMs,
            Truncate(entry.Error, 1000),
            entry.CorrelationId ?? current.CorrelationId,
            entry.Direction), cancellationToken);
    }

    public async Task LogExportAsync(ExportEntry entry, CancellationToken cancellationToken = default)
    {
        await PersistAsync(new ExportHistory(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.PipelineRunId,
            entry.DestinationName,
            entry.Format,
            entry.RowCount,
            entry.FileSizeBytes,
            entry.Status,
            entry.CorrelationId), cancellationToken);
    }

    public async Task LogDestinationActivityAsync(
        DestinationActivityEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        await PersistAsync(new DestinationActivityLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.DestinationId,
            Truncate(entry.DestinationName, 200)!,
            entry.DestinationType,
            entry.Stage,
            entry.Status,
            Truncate(entry.ResourceType, 100),
            entry.RecordCount,
            entry.WrittenCount,
            entry.DurationMs,
            Truncate(entry.Detail, 500),
            Truncate(entry.Error, 1000),
            entry.CorrelationId ?? current.CorrelationId,
            entry.PipelineRunId), cancellationToken);
    }

    public async Task LogNotificationAsync(NotificationEntry entry, CancellationToken cancellationToken = default)
    {
        await PersistAsync(new NotificationHistory(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.NotificationType,
            Truncate(entry.Recipient, 1000)!,
            Truncate(entry.Subject, 500),
            entry.Status,
            Truncate(entry.Error, 1000),
            entry.CorrelationId,
            Truncate(entry.Body, 8000),
            entry.AttachmentNames is { Count: > 0 } names ? Truncate(string.Join(", ", names), 1000) : null), cancellationToken);
    }

    public async Task LogValidationFailureAsync(ValidationFailureEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Warnings.Count == 0)
        {
            return;
        }

        await PersistAsync(new ValidationFailureLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.ResourceType,
            entry.ResourceId,
            Truncate(JsonSerializer.Serialize(entry.Warnings), 4000)!,
            entry.DataQualityScore,
            entry.PipelineRunId,
            entry.CorrelationId), cancellationToken);
    }

    public async Task LogEndpointHealthAsync(EndpointHealthEntry entry, CancellationToken cancellationToken = default)
    {
        await PersistAsync(new EndpointHealthCheck(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.EndpointName,
            entry.EndpointType,
            entry.Status,
            entry.LatencyMs,
            Truncate(entry.Message, 1000)), cancellationToken);
    }

    public async Task LogSmartLaunchAsync(SmartLaunchEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        await PersistAsync(new SmartLaunchLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.SourceConnectionId,
            entry.SourceName,
            entry.LaunchType,
            entry.Success,
            Truncate(entry.FailureReason, 1000),
            Truncate(entry.GrantedScope, 500),
            entry.PatientContextGranted,
            entry.TokenCacheKeyHash,
            entry.CorrelationId ?? current.CorrelationId), cancellationToken);
    }

    /// <summary>
    /// Adds one log row and saves it. A row that fails to save is detached before the exception is rethrown: this
    /// logger shares the request's (or workflow run's) DbContext, and a row left tracked as Added would be resent by
    /// every later SaveChanges in that scope. That is how one over-long authentication reason (a VPN-down token
    /// failure) made every later save of a workflow run fail, including the one marking the run Failed, so the run
    /// stayed "Running" for ever.
    /// </summary>
    private async Task PersistAsync(object entity, CancellationToken cancellationToken)
    {
        _dbContext.Add(entity);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
            throw;
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
