namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Ambient (AsyncLocal-flowed) label for "which automated process is currently running" — set by
/// non-interactive entry points (scheduler dispatch, webhook ingestion) so the Worker's otherwise-generic
/// "system" actor on <see cref="ICurrentUserService"/> can say which automation actually ran (e.g.
/// "Scheduler (Automated Pipeline Run)" vs "Webhook Ingestion (Automated)"), without threading an extra
/// parameter through every call in between. Stays null for interactive Api requests, which already have a
/// real authenticated user from the HTTP-aware <see cref="ICurrentUserService"/>.
/// </summary>
public interface IAmbientActorContext
{
    string? Current { get; }

    /// <summary>The correlation id of whatever automated run is currently executing (set alongside
    /// <see cref="Current"/> by the same <see cref="BeginScope"/> call), so a non-interactive
    /// <see cref="ICurrentUserService"/> (e.g. the Worker's, or the Api host's when running outside an HTTP
    /// request) can stamp AuditLog/AuthenticationLog/SecurityEvent/etc. with the run's real correlation id
    /// instead of leaving it null.</summary>
    string? CorrelationId { get; }

    /// <summary>Sets <see cref="Current"/> (and optionally <see cref="CorrelationId"/>) for the lifetime of the
    /// returned scope, restoring the previous values on dispose. Flows with the current async call chain only —
    /// does not cross a message-queue boundary, so it must be set inside the consumer that actually executes the
    /// work, not the dispatcher that enqueues it.</summary>
    IDisposable BeginScope(string actor, string? correlationId = null);
}
