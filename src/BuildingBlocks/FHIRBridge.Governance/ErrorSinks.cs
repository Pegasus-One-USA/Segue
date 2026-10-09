using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Governance;

/// <summary>
/// Writes an already-scrubbed <see cref="ErrorEntry"/> to the configured destination(s):
/// the <c>ErrorLogs</c> table (<see cref="IGovernanceLogger"/>), Application Insights, or both, per
/// <see cref="ErrorCaptureOptions.Sinks"/>.
/// </summary>
/// <summary>Optional: supplies a safe, scrubbed text timeline of the run behind a correlation id so it can be sent
/// to Application Insights along with the error. Implemented in the Application layer (it reads the governance tables).</summary>
public interface IErrorCorrelationSource
{
    Task<string?> GetTimelineTextAsync(string correlationId, CancellationToken cancellationToken);
}

public interface IErrorSinkRouter
{
    /// <summary>Writes to the table (when enabled — failures propagate so the caller can retry with a new reference
    /// id) and then, best-effort, to Application Insights.</summary>
    Task WriteAsync(ErrorEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Last resort when the table write failed on every attempt (e.g. database unreachable): sends to
    /// Application Insights only. Returns true if the error reached it.</summary>
    Task<bool> WriteFallbackAsync(ErrorEntry entry, CancellationToken cancellationToken = default);
}

public sealed class ErrorSinkRouter : IErrorSinkRouter
{
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ApplicationInsightsErrorSink _appInsights;
    private readonly ErrorCaptureOptions _options;
    private readonly IErrorCorrelationSource? _correlationSource;

    public ErrorSinkRouter(
        IGovernanceLogger governanceLogger,
        ApplicationInsightsErrorSink appInsights,
        IOptions<ErrorCaptureOptions> options,
        IErrorCorrelationSource? correlationSource = null)
    {
        _governanceLogger = governanceLogger;
        _appInsights = appInsights;
        _options = options.Value;
        _correlationSource = correlationSource;
    }

    // If Application Insights was requested but is not usable (no connection string / failed to initialise), the
    // table is used regardless — an error must never be configured out of existence.
    private bool TableEnabled => _options.WritesToTable || !_appInsights.IsAvailable;

    private bool AppInsightsEnabled => _options.WantsApplicationInsights && _appInsights.IsAvailable;

    public async Task WriteAsync(ErrorEntry entry, CancellationToken cancellationToken = default)
    {
        if (TableEnabled)
        {
            await _governanceLogger.LogErrorAsync(entry, cancellationToken);
        }

        if (AppInsightsEnabled)
        {
            _appInsights.TrySend(entry, await TryGetTimelineAsync(entry, cancellationToken));
        }
    }

    // The run timeline is attached to the first error of each correlation id inside a short window (a run that fails
    // 2,000 times must not cost 2,000 timeline queries). Best effort: any failure just means no timeline.
    private async Task<string?> TryGetTimelineAsync(ErrorEntry entry, CancellationToken cancellationToken)
    {
        if (_correlationSource is null
            || !_options.ApplicationInsights.IncludeCorrelationTimeline
            || string.IsNullOrWhiteSpace(entry.CorrelationId)
            || !_appInsights.ShouldAttachTimeline(entry.CorrelationId))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await _correlationSource.GetTimelineTextAsync(entry.CorrelationId, timeout.Token);
        }
        catch
        {
            return null;
        }
    }

    public Task<bool> WriteFallbackAsync(ErrorEntry entry, CancellationToken cancellationToken = default) =>
        Task.FromResult(AppInsightsEnabled && _appInsights.TrySend(entry));
}

/// <summary>
/// Sends scrubbed errors to Application Insights as exception telemetry (Failures blade), keyed by the same
/// <c>ErrorReferenceId</c> shown in the portal. Uses its own <see cref="TelemetryConfiguration"/> so it is
/// independent of the Serilog trace sink and the OpenTelemetry exporter. Never throws.
/// </summary>
public sealed class ApplicationInsightsErrorSink : IDisposable
{
    private const int MaxPropertyLength = 8000;
    private const int MaxStackChunks = 8;

    private readonly TelemetryConfiguration? _configuration;
    private readonly TelemetryClient? _client;

    public ApplicationInsightsErrorSink(IOptions<ErrorCaptureOptions> options)
    {
        var connectionString = options.Value.ApplicationInsights.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            _configuration = new TelemetryConfiguration { ConnectionString = connectionString };
            _client = new TelemetryClient(_configuration);
        }
        catch
        {
            _configuration?.Dispose();
            _configuration = null;
            _client = null;
        }
    }

    public bool IsAvailable => _client is not null;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _timelineSent = new();
    private static readonly TimeSpan TimelineWindow = TimeSpan.FromMinutes(5);

    /// <summary>True the first time a correlation id is seen inside the window (then records it).</summary>
    public bool ShouldAttachTimeline(string correlationId)
    {
        var now = DateTime.UtcNow;
        if (_timelineSent.Count > 2000)
        {
            foreach (var stale in _timelineSent.Where(kv => now - kv.Value > TimelineWindow).Select(kv => kv.Key).ToList())
            {
                _timelineSent.TryRemove(stale, out _);
            }
        }

        if (_timelineSent.TryGetValue(correlationId, out var last) && now - last < TimelineWindow)
        {
            return false;
        }

        _timelineSent[correlationId] = now;
        return true;
    }

    public bool TrySend(ErrorEntry entry, string? correlationTimeline = null)
    {
        if (_client is null) return false;

        try
        {
            ISupportProperties telemetry;
            var informational = string.Equals(entry.Severity, "Informational", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Severity, "Information", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Severity, "WorkflowDebug", StringComparison.OrdinalIgnoreCase);

            if (informational)
            {
                var trace = new TraceTelemetry($"{entry.ExceptionType}: {entry.Message}", SeverityLevel.Information);
                Apply(trace, entry);
                telemetry = trace;
                _client.TrackTrace(trace);
            }
            else
            {
                var exception = new ScrubbedException(entry.Message);
                var exceptionTelemetry = new ExceptionTelemetry(exception)
                {
                    SeverityLevel = MapSeverity(entry.Severity),
                };

                if (exceptionTelemetry.ExceptionDetailsInfoList.Count > 0)
                {
                    exceptionTelemetry.ExceptionDetailsInfoList[0].TypeName = entry.ExceptionType;
                    exceptionTelemetry.ExceptionDetailsInfoList[0].Message = entry.Message;
                }

                Apply(exceptionTelemetry, entry);
                telemetry = exceptionTelemetry;
                _client.TrackException(exceptionTelemetry);
            }

            AddProperties(telemetry, entry);
            AddChunked(telemetry.Properties, "CorrelationTimeline", correlationTimeline, 4);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Flushes buffered telemetry — called when the process is about to die.</summary>
    public void Flush()
    {
        try
        {
            _client?.Flush();
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        Flush();
        _configuration?.Dispose();
    }

    private static void Apply(ITelemetry telemetry, ErrorEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.TraceId))
        {
            telemetry.Context.Operation.Id = entry.TraceId;
        }

        if (!string.IsNullOrWhiteSpace(entry.SpanId))
        {
            telemetry.Context.Operation.ParentId = entry.SpanId;
        }

        if (!string.IsNullOrWhiteSpace(entry.Module))
        {
            telemetry.Context.Cloud.RoleName = entry.Module;
        }
    }

    private static void AddProperties(ISupportProperties telemetry, ErrorEntry entry)
    {
        var p = telemetry.Properties;
        Set(p, "ErrorReferenceId", entry.ErrorReferenceId);
        Set(p, "Category", entry.Category);
        Set(p, "Severity", entry.Severity);
        Set(p, "Module", entry.Module);
        Set(p, "ExceptionType", entry.ExceptionType);
        Set(p, "CorrelationId", entry.CorrelationId);
        Set(p, "ExecutionId", entry.ExecutionId);
        Set(p, "WorkflowId", entry.WorkflowId);
        Set(p, "EndpointId", entry.EndpointId);
        Set(p, "RequestId", entry.RequestId);
        Set(p, "DiagnosisAction", entry.DiagnosisAction?.ToString());
        Set(p, "DiagnosisCause", entry.DiagnosisCause);
        Set(p, "UserFriendlyMessage", entry.UserFriendlyMessage);
        Set(p, "WorkflowName", entry.WorkflowName);
        Set(p, "NodeName", entry.NodeName);
        Set(p, "NodeType", entry.NodeType);
        Set(p, "SourceName", entry.SourceName);
        Set(p, "DestinationName", entry.DestinationName);
        Set(p, "ResourceType", entry.ResourceType);

        // Application Insights caps a property value at 8192 chars; split the full scrubbed chain across chunks.
        AddChunked(p, "StackTrace", entry.StackTrace, MaxStackChunks);
    }

    private static void AddChunked(IDictionary<string, string> properties, string name, string? text, int maxChunks)
    {
        if (string.IsNullOrEmpty(text)) return;
        for (var i = 0; i < maxChunks && i * MaxPropertyLength < text.Length; i++)
        {
            var start = i * MaxPropertyLength;
            var length = Math.Min(MaxPropertyLength, text.Length - start);
            properties[i == 0 ? name : $"{name}.{i}"] = text.Substring(start, length);
        }
    }

    private static void Set(IDictionary<string, string> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[key] = value.Length > MaxPropertyLength ? value[..MaxPropertyLength] : value;
        }
    }

    private static SeverityLevel MapSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" or "fatal" => SeverityLevel.Critical,
        "warning" => SeverityLevel.Warning,
        "informational" or "information" => SeverityLevel.Information,
        _ => SeverityLevel.Error,
    };

    /// <summary>Carrier for an already-scrubbed message; never wraps the original (PHI-bearing) exception.</summary>
    private sealed class ScrubbedException : Exception
    {
        public ScrubbedException(string message) : base(message)
        {
        }
    }
}
