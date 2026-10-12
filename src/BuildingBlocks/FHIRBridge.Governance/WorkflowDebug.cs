using System.Diagnostics;
using FHIRBridge.Observability.Logging;

namespace FHIRBridge.Governance;

/// <summary>
/// Step-by-step trace of a workflow run, written to the error log as entries of type <c>WorkflowDebug</c> so that,
/// when something breaks, you can see exactly which step it reached. Each line carries the workflow name and id, the
/// execution id, the step position ("Step 2/5"), the stage (Source / Transform / Destination ...) and the node,
/// source and destination names that were in play.
/// <para>Off unless an admin enables the "WorkflowDebug" entry type in Error Log Handling; when off, <see cref="Write"/>
/// is a single volatile read.</para>
/// </summary>
public static class WorkflowDebug
{
    public const string Severity = "WorkflowDebug";

    /// <summary>Most resource-level lines one run may write (plus <see cref="MaxResourceFailureLinesPerRun"/> failures).</summary>
    public const int MaxResourceLinesPerRun = 300;
    public const int MaxResourceFailureLinesPerRun = 300;

    /// <summary>True when the trace should include the stages inside a step.</summary>
    public static bool StagesEnabled => IsEnabled && ErrorCaptureRelay.WorkflowDebugLevel >= 2;

    /// <summary>True when the trace should include individual resources.</summary>
    public static bool ResourcesEnabled => IsEnabled && ErrorCaptureRelay.WorkflowDebugLevel >= 3;

    public static bool IsEnabled => ErrorCaptureRelay.WorkflowDebugEnabled && ErrorCaptureRelay.Handler is not null;

    /// <summary>Writes a trace line using the names of the current <see cref="ErrorContext"/> scope.</summary>
    public static void Write(string message) => Write(message, ErrorContext.Current);

    /// <summary>Writes a trace line using the supplied context (e.g. a failed step's pinned snapshot).</summary>
    public static void Write(string message, ErrorContext? context)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var activity = Activity.Current;
            ErrorCaptureRelay.Handler?.Invoke(new RelayedLogError(
                DateTime.UtcNow,
                Severity,
                Severity,
                message,
                null,
                activity?.TraceId.ToString(),
                activity?.SpanId.ToString(),
                context?.ToProperties()));
        }
        catch
        {
            // A trace line must never disturb the run it describes.
        }
    }

    /// <summary>A line for a stage inside a step (needs the "Stages" detail level or higher).</summary>
    public static void Stage(string message)
    {
        if (StagesEnabled)
        {
            Write(message, ErrorContext.Current);
        }
    }

    /// <summary>A line about one individual resource (needs the "Resources" detail level). Successes are limited to
    /// <see cref="MaxResourceLinesPerRun"/> per run; failures are always written, up to their own limit.</summary>
    public static void Resource(string message, bool isFailure = false) => Resource(() => message, isFailure);

    /// <summary>Same, but the text is only built once the line is known to be within budget - use this on per-resource
    /// paths so the cap also saves the string work.</summary>
    public static void Resource(Func<string> message, bool isFailure = false)
    {
        if (!ResourcesEnabled)
        {
            return;
        }

        var context = ErrorContext.Current;
        var counter = context?.ResourceLines;
        var limit = isFailure ? MaxResourceFailureLinesPerRun : MaxResourceLinesPerRun;
        var used = counter is not null
            ? (isFailure ? Interlocked.Increment(ref counter.Failures) : Interlocked.Increment(ref counter.Written))
            : FallbackUsed(isFailure);
        if (used > limit)
        {
            return;
        }

        Write(message(), context);
    }

    // Code running outside any workflow scope (no ErrorContext) still gets a cap: per minute, per process.
    private static readonly object FallbackLock = new();
    private static long _fallbackWindowMinute;
    private static int _fallbackWritten;
    private static int _fallbackFailures;

    private static int FallbackUsed(bool isFailure)
    {
        lock (FallbackLock)
        {
            var minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            if (minute != _fallbackWindowMinute)
            {
                _fallbackWindowMinute = minute;
                _fallbackWritten = 0;
                _fallbackFailures = 0;
            }

            return isFailure ? ++_fallbackFailures : ++_fallbackWritten;
        }
    }
}
