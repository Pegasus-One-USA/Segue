using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Governance;

/// <summary>
/// Catch-all that sits beside the existing explicit <see cref="IGlobalExceptionManager"/> call sites — it changes
/// none of them. It records failures nothing else routed through the manager:
/// <list type="bullet">
/// <item>process-level <c>AppDomain.UnhandledException</c> / <c>TaskScheduler.UnobservedTaskException</c>;</item>
/// <item>Error/Fatal log events (via the Serilog relay) whose exception was never captured by its own call site.</item>
/// </list>
/// Everything flows into the same manager, so scrubbing, the configured sinks, classification and reference ids
/// behave identically to an explicitly captured error.
/// </summary>
public sealed class AmbientErrorCaptureService : IHostedService, IDisposable
{
    private sealed record Pending(
        DateTime OccurredUtc,
        Exception Exception,
        string Module,
        string Severity,
        string? TraceId,
        string? SpanId,
        string? SwallowedArea = null,
        bool IsTrace = false,
        IReadOnlyDictionary<string, string?>? Properties = null);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ApplicationInsightsErrorSink _appInsights;
    private readonly ILogger<AmbientErrorCaptureService> _logger;
    private readonly IErrorCapturePolicy? _policy;
    private readonly IErrorScrubber _scrubber;
    private Action<RelayedLogError>? _handler;
    private long _lowWindowStartTicks;
    private int _lowCountInWindow;
    private readonly AmbientErrorCaptureOptions _options;
    private readonly Channel<Pending> _channel = Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    // Workflow trace lines must never wait behind errors that are sitting out the dedupe window, so they have their own
    // queue and consumer. Both queues report drops instead of discarding silently.
    private readonly Channel<Pending> _traceChannel = Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private Task? _traceConsumer;
    private long _dropped;
    private readonly ConcurrentDictionary<string, DateTime> _recent = new();

    private Task? _consumer;
    private CancellationTokenSource? _stopping;
    private volatile bool _draining;

    public AmbientErrorCaptureService(
        IServiceScopeFactory scopeFactory,
        ApplicationInsightsErrorSink appInsights,
        IOptions<ErrorCaptureOptions> options,
        ILogger<AmbientErrorCaptureService> logger,
        IErrorCapturePolicy? policy = null,
        IErrorScrubber? scrubber = null)
    {
        _logger = logger;
        _scrubber = scrubber ?? new ErrorScrubber();
        _policy = policy;
        _scopeFactory = scopeFactory;
        _appInsights = appInsights;
        _options = options.Value.Ambient;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        _stopping = new CancellationTokenSource();

        if (_options.CaptureProcessLevelExceptions)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        // The relay also carries the workflow trace (WorkflowDebug) and swallowed-error reports, which are not "logged
        // errors", so it is always set up. CaptureLoggedErrors only decides whether Error/Warning/Information LOG LINES
        // are captured (see OnLogError).
        _handler = OnLogError;
        ErrorCaptureRelay.Handler = _handler;
        _consumer = Task.Run(() => ConsumeAsync(_stopping.Token));
        _traceConsumer = Task.Run(() => ConsumeTraceAsync(_stopping.Token));
        _ = Task.Run(() => KeepLevelInSyncAsync(_stopping.Token));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        // The relay is process-wide: only stand down if it is still wired to THIS instance (a second host in the same
        // process - e.g. a test server - may have taken it over).
        if (_handler is not null && ReferenceEquals(ErrorCaptureRelay.Handler, _handler))
        {
            ErrorCaptureRelay.Handler = null;
            ErrorCaptureRelay.MinimumLevelValue = 4;
            ErrorCaptureRelay.WorkflowDebugEnabled = false;
        }

        // Drain: no new events can arrive now, so let both consumers finish what is already queued (without the dedupe
        // wait) before the loops are cancelled. Bounded, so a slow database cannot hold shutdown up.
        _draining = true;
        _channel.Writer.TryComplete();
        _traceChannel.Writer.TryComplete();

        try
        {
            var pending = Task.WhenAll(_consumer ?? Task.CompletedTask, _traceConsumer ?? Task.CompletedTask);
            await pending.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        }
        catch
        {
            // shutting down; whatever is still queued after the grace period is dropped
        }

        _stopping?.Cancel();
    }

    public void Dispose() => _stopping?.Dispose();

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex || CapturedExceptionRegistry.WasCaptured(ex)) return;

        // The process may be about to die: capture synchronously (bounded) and flush Application Insights.
        var task = Task.Run(() => CaptureAsync(
            ex, "Process.UnhandledException", e.IsTerminating ? "Critical" : "Error", Activity.Current));
        task.Wait(TimeSpan.FromSeconds(5));
        _appInsights.Flush();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (CapturedExceptionRegistry.WasCaptured(e.Exception)) return;
        _ = Task.Run(() => CaptureAsync(e.Exception, "Process.UnobservedTask", "Error", Activity.Current));
    }

    /// <summary>Lowers / raises the relay's minimum level to match the error-log settings (Warning / Information
    /// entries only reach the capture while an admin has enabled them).</summary>
    private async Task KeepLevelInSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_policy is not null)
            {
                await _policy.RefreshAsync(cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = _policy?.Current ?? ErrorLogSettings.Default;
                ErrorCaptureRelay.MinimumLevelValue = !_options.CaptureLoggedErrors ? 4 : settings.CapturesInformation ? 2 : settings.CapturesWarnings ? 3 : 4;
                ErrorCaptureRelay.WorkflowDebugEnabled = settings.CapturesWorkflowDebug;
                ErrorCaptureRelay.WorkflowDebugLevel = settings.WorkflowDebugLevel;
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    _logger.LogWarning(
                        "{Dropped} error / workflow-trace event(s) were dropped because capture could not keep up; they are not in the error log.",
                        dropped);
                }

                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch
        {
            // the level simply stays where it was
        }
    }

    private bool AllowLowSeverity()
    {
        var max = _options.MaxLowSeverityPerMinute;
        if (max <= 0) return true;
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lowWindowStartTicks) > TimeSpan.TicksPerMinute)
        {
            Interlocked.Exchange(ref _lowWindowStartTicks, now);
            Interlocked.Exchange(ref _lowCountInWindow, 0);
        }

        return Interlocked.Increment(ref _lowCountInWindow) <= max;
    }

    private void OnLogError(RelayedLogError log)
    {
        // Step-by-step workflow trace lines (WorkflowDebug.Write): their own entry type, switched on in the settings.
        if (string.Equals(log.Level, WorkflowDebug.Severity, StringComparison.Ordinal))
        {
            if (!(_policy?.Current ?? ErrorLogSettings.Default).CapturesWorkflowDebug) return;
            if (!_traceChannel.Writer.TryWrite(new Pending(
                log.OccurredUtc, new LogEntry(log.Message), "Workflow", WorkflowDebug.Severity,
                log.TraceId, log.SpanId, IsTrace: true, Properties: log.Properties)))
            {
                Interlocked.Increment(ref _dropped);
            }

            return;
        }

        var category = log.Category ?? string.Empty;
        var isSwallowedReport = category.StartsWith(SwallowedError.CategoryPrefix, StringComparison.Ordinal);
        if (!_options.CaptureLoggedErrors && !isSwallowedReport) return;
        var isLowSeverity = string.Equals(log.Level, "Warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(log.Level, "Information", StringComparison.OrdinalIgnoreCase);
        if (isLowSeverity)
        {
            // Warning / Information entries are opt-in (error-log settings) and rate-limited.
            var settings = _policy?.Current ?? ErrorLogSettings.Default;
            if (!settings.ShouldCapture(log.Level, null) || !AllowLowSeverity()) return;
        }

        foreach (var prefix in _options.ExcludedCategoryPrefixes)
        {
            if (category.StartsWith(prefix, StringComparison.Ordinal)) return;
        }

        var exception = log.Exception;
        if (exception is null)
        {
            if (!isLowSeverity && !_options.CaptureLogErrorsWithoutException) return;
            exception = new LogEntry(log.Message);
        }

        // Errors a catch block chose to swallow (SwallowedError.Report) arrive with a "Swallowed:<area>" category.
        var swallowedArea = category.StartsWith(SwallowedError.CategoryPrefix, StringComparison.Ordinal)
            ? category[SwallowedError.CategoryPrefix.Length..]
            : null;
        var module = swallowedArea is not null ? category : $"Log:{category}";
        if (!_channel.Writer.TryWrite(new Pending(
            log.OccurredUtc,
            exception,
            module.Length > 100 ? module[..100] : module,
            log.Level.ToLowerInvariant() switch
            {
                "fatal" => "Critical",
                "warning" => "Warning",
                "information" => "Information",
                _ => "Error",
            },
            log.TraceId,
            log.SpanId,
            swallowedArea)))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task ConsumeTraceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _traceChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await WriteTraceAsync(item);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromSeconds(Math.Max(0, _options.DedupeWindowSeconds));

        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.IsTrace)
                {
                    await WriteTraceAsync(item);
                    continue;
                }

                // Call sites typically log and then hand the same exception to the manager a moment later.
                var wait = item.OccurredUtc + window - DateTime.UtcNow;
                if (wait > TimeSpan.Zero && !_draining)
                {
                    await Task.Delay(wait, cancellationToken);
                }

                if (CapturedExceptionRegistry.WasCaptured(item.Exception) || IsRepeat(item))
                {
                    continue;
                }

                if (item.SwallowedArea is not null)
                {
                    // Make it visible in the application log too (console / file / Seq / Application Insights traces).
                    // Suppressed so this log line is not captured a second time.
                    ErrorCaptureRelay.IsSuppressed = true;
                    try
                    {
                        // Scrubbed BEFORE it is logged (the stored copy is scrubbed again by the exception manager). The exception
                        // object is deliberately not passed: its own message and stack would reach the sinks unscrubbed.
                        _logger.LogError(
                            "Swallowed error in {Area}: {ExceptionType}: {Message}",
                            item.SwallowedArea, item.Exception.GetType().Name, _scrubber.ScrubText(item.Exception.Message));
                    }
                    finally
                    {
                        ErrorCaptureRelay.IsSuppressed = false;
                    }
                }

                await CaptureAsync(item.Exception, item.Module, item.Severity, traceId: item.TraceId, spanId: item.SpanId);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>Writes one workflow trace line: to the application log (console / file / Seq) and to the error log.</summary>
    private async Task WriteTraceAsync(Pending item)
    {
        string? Prop(string key) =>
            item.Properties is not null && item.Properties.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        ErrorCaptureRelay.IsSuppressed = true;
        try
        {
            _logger.LogInformation("WorkflowDebug: {Message}", _scrubber.ScrubText(item.Exception.Message));

            using var scope = _scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await manager.CaptureTraceAsync(
                WorkflowDebug.Severity,
                WorkflowDebug.Severity,
                item.Exception.Message,
                new ExceptionContext(
                    Module: "Workflow",
                    Severity: WorkflowDebug.Severity,
                    CorrelationId: Prop("CorrelationId"),
                    ExecutionId: Prop("ExecutionId"),
                    WorkflowId: Prop("WorkflowId"),
                    TraceId: item.TraceId,
                    SpanId: item.SpanId,
                    WorkflowName: Prop("WorkflowName"),
                    NodeName: Prop("NodeName"),
                    NodeType: Prop("NodeType"),
                    SourceName: Prop("SourceName"),
                    DestinationName: Prop("DestinationName"),
                    ResourceType: Prop("ResourceType")),
                item.OccurredUtc);
        }
        catch
        {
            // A trace line must never fault the host.
        }
        finally
        {
            ErrorCaptureRelay.IsSuppressed = false;
        }
    }

    private bool IsRepeat(Pending item)
    {
        var suppress = TimeSpan.FromSeconds(Math.Max(0, _options.RepeatSuppressionSeconds));
        if (suppress == TimeSpan.Zero) return false;

        var message = item.Exception.Message;
        // The whole message, not a prefix: two different errors that merely start alike must both be recorded.
        var key = $"{item.Exception.GetType().Name}|{item.Module}|{System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(message)).Aggregate(0L, (h, b) => h * 31 + b)}|{message.Length}";
        var now = DateTime.UtcNow;

        if (_recent.Count > 1000)
        {
            foreach (var stale in _recent.Where(kv => now - kv.Value > suppress).Select(kv => kv.Key).ToList())
            {
                _recent.TryRemove(stale, out _);
            }
        }

        if (_recent.TryGetValue(key, out var last) && now - last < suppress)
        {
            return true;
        }

        _recent[key] = now;
        return false;
    }

    private Task CaptureAsync(Exception exception, string module, string severity, Activity? activity) =>
        CaptureAsync(exception, module, severity, activity?.TraceId.ToString(), activity?.SpanId.ToString());

    private async Task CaptureAsync(Exception exception, string module, string severity, string? traceId, string? spanId)
    {
        // Anything logged while capturing (e.g. DB unreachable) must not be re-captured.
        ErrorCaptureRelay.IsSuppressed = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await manager.CaptureAsync(
                exception,
                new ExceptionContext(
                    Module: module,
                    Severity: severity,
                    CorrelationId: traceId,
                    TraceId: traceId,
                    SpanId: spanId));
        }
        catch
        {
            // Capturing must never fault the host.
        }
        finally
        {
            ErrorCaptureRelay.IsSuppressed = false;
        }
    }

    /// <summary>Stand-in for an Error log event that had no exception object.</summary>
    private sealed class LogEntry : Exception
    {
        public LogEntry(string message) : base(message)
        {
        }
    }
}
