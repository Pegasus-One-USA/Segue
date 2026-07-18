namespace FHIRBridge.Governance;

/// <summary>
/// Single entry point every module writes governance events through. One method per event
/// category (not per entity/resource type) — new resource types or config entities never require
/// a new method here, only a new call site passing the right entry.
/// </summary>
public interface IGovernanceLogger
{
    Task LogAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task LogDataAccessAsync(DataAccessEntry entry, CancellationToken cancellationToken = default);

    Task LogAuthenticationAsync(AuthenticationEntry entry, CancellationToken cancellationToken = default);

    Task LogSecurityEventAsync(SecurityEventEntry entry, CancellationToken cancellationToken = default);

    Task LogAuthorizationAsync(AuthorizationEntry entry, CancellationToken cancellationToken = default);

    Task LogSchedulerRunAsync(SchedulerRunEntry entry, CancellationToken cancellationToken = default);

    Task LogRetryAsync(RetryEntry entry, CancellationToken cancellationToken = default);

    Task LogErrorAsync(ErrorEntry entry, CancellationToken cancellationToken = default);

    Task LogApiRequestAsync(ApiRequestEntry entry, CancellationToken cancellationToken = default);

    Task LogExportAsync(ExportEntry entry, CancellationToken cancellationToken = default);

    Task LogNotificationAsync(NotificationEntry entry, CancellationToken cancellationToken = default);

    Task LogValidationFailureAsync(ValidationFailureEntry entry, CancellationToken cancellationToken = default);

    Task LogEndpointHealthAsync(EndpointHealthEntry entry, CancellationToken cancellationToken = default);

    Task LogSmartLaunchAsync(SmartLaunchEntry entry, CancellationToken cancellationToken = default);
}
