using Serilog.Core;
using Serilog.Events;

namespace FHIRBridge.Observability.Logging;

/// <summary>An Error/Fatal log event relayed out of Serilog so the Governance layer can capture it centrally.</summary>
public sealed record RelayedLogError(
    DateTime OccurredUtc,
    string Level,
    string? Category,
    string Message,
    Exception? Exception,
    string? TraceId,
    string? SpanId,
    IReadOnlyDictionary<string, string?>? Properties = null);

/// <summary>
/// Thin bridge between the Serilog pipeline (Observability, which cannot reference Governance) and the central
/// error-capture service. Governance sets <see cref="Handler"/> at host start; until then (or when ambient capture is
/// disabled) the sink is a no-op. <see cref="IsSuppressed"/> lets the capture service stop its own work from being
/// re-captured (capture → DB failure → LogError → capture …).
/// </summary>
public static class ErrorCaptureRelay
{
    private static readonly AsyncLocal<bool> Suppressed = new();

    public static Action<RelayedLogError>? Handler { get; set; }

    /// <summary>Lowest Serilog level (0 Verbose ... 2 Information, 3 Warning, 4 Error, 5 Fatal) the capture wants.
    /// Defaults to Error; the capture service lowers it when the error-log settings ask for Warning / Information
    /// entries. Checked before anything is rendered, so the sink is free for the vast majority of log events.</summary>
    public static volatile int MinimumLevelValue = 4;

    /// <summary>True while an admin has enabled the "WorkflowDebug" entry type; checked before building any trace line.</summary>
    public static volatile bool WorkflowDebugEnabled;

    /// <summary>1 = Steps, 2 = Stages, 3 = Resources - how detailed the workflow trace is.</summary>
    public static volatile int WorkflowDebugLevel = 1;

    public static bool IsSuppressed
    {
        get => Suppressed.Value;
        set => Suppressed.Value = value;
    }
}

/// <summary>Serilog sink that forwards Error-and-above events to <see cref="ErrorCaptureRelay.Handler"/>.</summary>
public sealed class ErrorCaptureRelaySink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var handler = ErrorCaptureRelay.Handler;
        if (handler is null || (int)logEvent.Level < ErrorCaptureRelay.MinimumLevelValue || ErrorCaptureRelay.IsSuppressed)
        {
            return;
        }

        try
        {
            string? category = null;
            if (logEvent.Properties.TryGetValue("SourceContext", out var source) && source is ScalarValue { Value: string s })
            {
                category = s;
            }

            // Below Error only the application's own log lines are of interest, never framework chatter.
            if ((int)logEvent.Level < 4 && category is not null && !category.StartsWith("FHIRBridge", StringComparison.Ordinal))
            {
                return;
            }

            handler(new RelayedLogError(
                logEvent.Timestamp.UtcDateTime,
                logEvent.Level.ToString(),
                category,
                logEvent.RenderMessage(),
                logEvent.Exception,
                logEvent.TraceId?.ToString(),
                logEvent.SpanId?.ToString()));
        }
        catch
        {
            // A logging sink must never throw into the caller.
        }
    }
}
