using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Enrichers.Span;
using Serilog.Events;

namespace FHIRBridge.Observability.Logging;

/// <summary>
/// Centralized Serilog configuration shared by every FHIRBridge host (API, Worker, ControlPlane, Runtime). Wires the
/// console sink plus a Seq sink (dev) and Azure Application Insights sink (prod) when configured, enables
/// <see cref="LogContext"/> correlation enrichment (PipelineRunId/CorrelationId), and applies a PHI-masking
/// enricher so structured log properties never leak patient identifiers.
/// </summary>
public static class FhirBridgeLogging
{
    /// <summary>
    /// Configures the logger. Reads simple keys:
    /// <list type="bullet">
    /// <item><c>Observability:SeqServerUrl</c> — when set, logs are also sent to Seq (e.g. http://localhost:5341).</item>
    /// <item><c>ApplicationInsights:ConnectionString</c> — when set, logs are also sent to Azure Monitor.</item>
    /// <item><c>Observability:LogFilePath</c> — when set, logs are also written to a rolling file at this path
    /// (e.g. <c>logs/fhirbridge-worker-.log</c> — the dash before the extension is where Serilog inserts the date).
    /// Needed for hosts running as a Windows Service/systemd unit with no attached console.</item>
    /// <item><c>Observability:Phi:MaskedProperties</c> — optional comma-separated override of masked property names.</item>
    /// </list>
    /// Minimum-level overrides keep EF Core / framework noise out of the structured logs by default; the
    /// <c>Serilog</c> configuration section (via <c>ReadFrom.Configuration</c>) can still override anything.
    /// </summary>
    public static LoggerConfiguration ConfigureFhirBridge(
        this LoggerConfiguration logger,
        IConfiguration configuration,
        string serviceName)
    {
        logger
            .ReadFrom.Configuration(configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            .Enrich.WithProperty("Application", serviceName)
            .Enrich.With(new PhiMaskingEnricher(configuration))
            .WriteTo.Console();

        var seqUrl = configuration["Observability:SeqServerUrl"];
        if (!string.IsNullOrWhiteSpace(seqUrl))
        {
            logger.WriteTo.Seq(seqUrl);
        }

        var logFilePath = configuration["Observability:LogFilePath"];
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            // A relative path resolves against the process's current working directory, not the executable's
            // folder -- for a Windows Service started via the SCM (New-Service/sc.exe, no explicit working
            // directory), that CWD defaults to C:\Windows\System32, not the deploy folder. Anchor explicitly to
            // AppContext.BaseDirectory so the log always lands next to the exe/dll regardless of host (Windows
            // Service, systemd, IIS, `dotnet run`).
            var resolvedLogFilePath = Path.IsPathRooted(logFilePath)
                ? logFilePath
                : Path.Combine(AppContext.BaseDirectory, logFilePath);

            logger.WriteTo.File(
                resolvedLogFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true);
        }

        var appInsightsConnection = configuration["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnection))
        {
            var telemetry = new TelemetryConfiguration { ConnectionString = appInsightsConnection };
            logger.WriteTo.ApplicationInsights(telemetry, TelemetryConverter.Traces);
        }

        return logger;
    }
}

/// <summary>
/// Masks structured log properties whose name matches a PHI-sensitive set so accidental PHI in log scopes/objects is
/// never persisted to a sink. Defensive — the audit/lineage paths are already PHI-free; this guards ad-hoc logging.
/// HIPAA #9: recurses into destructured (<c>{@Foo}</c>) object graphs rather than only top-level properties, and
/// separately redacts the exception message/stack trace when a log event carries one — those previously reached
/// sinks entirely unmasked.
/// </summary>
public sealed class PhiMaskingEnricher : ILogEventEnricher
{
    private const string Mask = "***";
    private const int MaxRecursionDepth = 5;

    private readonly IPhiRedactor _redactor;

    public PhiMaskingEnricher(IConfiguration configuration)
        : this(new PhiRedactor(configuration))
    {
    }

    public PhiMaskingEnricher(IPhiRedactor redactor)
    {
        _redactor = redactor;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var name in logEvent.Properties.Keys.ToList())
        {
            var masked = MaskValue(name, logEvent.Properties[name], propertyFactory, depth: 0);
            if (!ReferenceEquals(masked, logEvent.Properties[name]))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, masked));
            }
        }

        if (logEvent.Exception is not null)
        {
            var maskedMessage = _redactor.Redact(logEvent.Exception.Message);
            var maskedDetails = _redactor.Redact(logEvent.Exception.ToString());

            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("MaskedExceptionMessage", maskedMessage));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("MaskedExceptionDetails", maskedDetails));
        }

        // Interpolated values can bypass structured properties entirely (e.g. string-concatenated messages) —
        // this covers that gap without needing to alter the original message template.
        var maskedRendered = _redactor.Redact(logEvent.RenderMessage());
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("MaskedRenderedMessage", maskedRendered));
    }

    private LogEventPropertyValue MaskValue(
        string name, LogEventPropertyValue value, ILogEventPropertyFactory propertyFactory, int depth)
    {
        if (_redactor.IsSensitive(name))
        {
            return new ScalarValue(Mask);
        }

        if (depth >= MaxRecursionDepth)
        {
            return value;
        }

        switch (value)
        {
            case StructureValue structure:
                var maskedProperties = structure.Properties
                    .Select(p => new LogEventProperty(p.Name, MaskValue(p.Name, p.Value, propertyFactory, depth + 1)))
                    .ToList();
                return new StructureValue(maskedProperties, structure.TypeTag);

            case SequenceValue sequence:
                var maskedElements = sequence.Elements
                    .Select(e => MaskValue(name, e, propertyFactory, depth + 1))
                    .ToList();
                return new SequenceValue(maskedElements);

            case DictionaryValue dictionary:
                var maskedEntries = dictionary.Elements.Select(kvp =>
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        kvp.Key, MaskValue(kvp.Key.Value?.ToString() ?? name, kvp.Value, propertyFactory, depth + 1)));
                return new DictionaryValue(maskedEntries);

            default:
                return value;
        }
    }
}
