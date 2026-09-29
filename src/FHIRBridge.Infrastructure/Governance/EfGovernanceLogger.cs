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

        _dbContext.AuditLogs.Add(new AuditLog(
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
            current.UserAgent,
            entry.CorrelationId ?? current.CorrelationId,
            previousHash));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogDataAccessAsync(DataAccessEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        _dbContext.DataAccessLogs.Add(new DataAccessLog(
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
            current.IpAddress));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogAuthenticationAsync(AuthenticationEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        _dbContext.AuthenticationLogs.Add(new AuthenticationLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.UserEmail ?? current.Email,
            entry.AuthenticationType,
            entry.Success,
            entry.FailureReason,
            current.IpAddress,
            current.UserAgent,
            entry.CorrelationId ?? current.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogSecurityEventAsync(SecurityEventEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        _dbContext.SecurityEvents.Add(new SecurityEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.Severity,
            entry.EventType,
            entry.UserEmail ?? current.Email,
            current.IpAddress,
            entry.Details,
            entry.CorrelationId ?? current.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogAuthorizationAsync(AuthorizationEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        _dbContext.AuthorizationLogs.Add(new AuthorizationLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.UserEmail ?? current.Email,
            Truncate(entry.RequestPath, 500)!,
            entry.PermissionCode,
            entry.Result,
            current.IpAddress,
            entry.CorrelationId ?? current.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogSchedulerRunAsync(SchedulerRunEntry entry, CancellationToken cancellationToken = default)
    {
        _dbContext.SchedulerHistory.Add(new SchedulerHistory(
            Guid.NewGuid(),
            entry.SchedulerId,
            DateTime.UtcNow,
            entry.Status,
            entry.RouteCount,
            entry.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogRetryAsync(RetryEntry entry, CancellationToken cancellationToken = default)
    {
        _dbContext.RetryHistory.Add(new RetryHistory(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.Context,
            entry.RetryNumber,
            entry.DelayMilliseconds,
            entry.Reason,
            entry.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
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

        _dbContext.ApiRequestLogs.Add(new ApiRequestLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.Method,
            Truncate(entry.Url, 1000)!,
            entry.StatusCode,
            entry.DurationMs,
            Truncate(entry.Error, 1000),
            entry.CorrelationId ?? current.CorrelationId,
            entry.Direction));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogExportAsync(ExportEntry entry, CancellationToken cancellationToken = default)
    {
        _dbContext.ExportHistory.Add(new ExportHistory(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.PipelineRunId,
            entry.DestinationName,
            entry.Format,
            entry.RowCount,
            entry.FileSizeBytes,
            entry.Status,
            entry.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogNotificationAsync(NotificationEntry entry, CancellationToken cancellationToken = default)
    {
        _dbContext.NotificationHistory.Add(new NotificationHistory(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.NotificationType,
            Truncate(entry.Recipient, 1000)!,
            Truncate(entry.Subject, 500),
            entry.Status,
            Truncate(entry.Error, 1000),
            entry.CorrelationId,
            Truncate(entry.Body, 8000),
            entry.AttachmentNames is { Count: > 0 } names ? Truncate(string.Join(", ", names), 1000) : null));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogValidationFailureAsync(ValidationFailureEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Warnings.Count == 0)
        {
            return;
        }

        _dbContext.ValidationFailureLogs.Add(new ValidationFailureLog(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.ResourceType,
            entry.ResourceId,
            Truncate(JsonSerializer.Serialize(entry.Warnings), 4000)!,
            entry.DataQualityScore,
            entry.PipelineRunId,
            entry.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogEndpointHealthAsync(EndpointHealthEntry entry, CancellationToken cancellationToken = default)
    {
        _dbContext.EndpointHealthChecks.Add(new EndpointHealthCheck(
            Guid.NewGuid(),
            DateTime.UtcNow,
            entry.EndpointName,
            entry.EndpointType,
            entry.Status,
            entry.LatencyMs,
            Truncate(entry.Message, 1000)));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LogSmartLaunchAsync(SmartLaunchEntry entry, CancellationToken cancellationToken = default)
    {
        var current = _currentUserService.CurrentUser;

        _dbContext.SmartLaunchLogs.Add(new SmartLaunchLog(
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
            entry.CorrelationId ?? current.CorrelationId));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
