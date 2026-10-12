using System.Diagnostics;
using FHIRBridge.Observability.Logging;

namespace FHIRBridge.Governance;

/// <summary>
/// For the few places that must keep going after an exception (best-effort work, per-item fallbacks) but should
/// still leave a trace: call <see cref="Report"/> from the <c>catch</c> block. The failure is recorded through the
/// same central capture as every other error (scrubbed, named with the workflow / node / source / destination it
/// happened in, written to the configured sinks) and also written to the application log. It never throws and never
/// changes the caller's control flow - the catch block still decides what happens next.
/// </summary>
public static class SwallowedError
{
    public const string CategoryPrefix = "Swallowed:";

    /// <param name="exception">The exception being swallowed.</param>
    /// <param name="area">Short, stable label for where it happened, e.g. <c>Source.PractitionerRole search</c>.</param>
    public static void Report(Exception exception, string area)
    {
        try
        {
            // Pin the names now: the capture runs later on another thread, outside this node's scope.
            ErrorContext.Remember(exception);

            var activity = Activity.Current;
            ErrorCaptureRelay.Handler?.Invoke(new RelayedLogError(
                DateTime.UtcNow,
                "Error",
                CategoryPrefix + area,
                exception.Message,
                exception,
                activity?.TraceId.ToString(),
                activity?.SpanId.ToString()));
        }
        catch
        {
            // Reporting must never disturb the code that is already recovering from an error.
        }
    }
}
