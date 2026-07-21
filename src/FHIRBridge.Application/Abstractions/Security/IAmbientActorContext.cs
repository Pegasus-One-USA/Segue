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

    /// <summary>Sets <see cref="Current"/> for the lifetime of the returned scope, restoring the previous
    /// value on dispose. Flows with the current async call chain only — does not cross a message-queue
    /// boundary, so it must be set inside the consumer that actually executes the work, not the dispatcher
    /// that enqueues it.</summary>
    IDisposable BeginScope(string actor);
}
