using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No-op logger for the in-memory (no-database) dev configuration — there is no store to write to.</summary>
public sealed class NullGovernanceLogger : IGovernanceLogger
{
    public Task LogAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogDataAccessAsync(DataAccessEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogAuthenticationAsync(AuthenticationEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogSecurityEventAsync(SecurityEventEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogAuthorizationAsync(AuthorizationEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogSchedulerRunAsync(SchedulerRunEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogRetryAsync(RetryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogErrorAsync(ErrorEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogApiRequestAsync(ApiRequestEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogExportAsync(ExportEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogNotificationAsync(NotificationEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogValidationFailureAsync(ValidationFailureEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogEndpointHealthAsync(EndpointHealthEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogSmartLaunchAsync(SmartLaunchEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
