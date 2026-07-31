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
            logger.WriteTo.File(
                logFilePath,
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
/// </summary>
public sealed class PhiMaskingEnricher : ILogEventEnricher
{
    private const string Mask = "***";

    private static readonly string[] DefaultMasked =
    [
        "ssn", "mrn", "birthDate", "birthdate", "email", "phone", "telecom",
        "givenName", "familyName", "patientName", "name", "address", "postalCode", "identifierValue"
    ];

    private readonly HashSet<string> _masked;

    public PhiMaskingEnricher(IConfiguration configuration)
    {
        var configured = configuration["Observability:Phi:MaskedProperties"];
        var names = string.IsNullOrWhiteSpace(configured)
            ? DefaultMasked
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _masked = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var name in logEvent.Properties.Keys.ToList())
        {
            if (_masked.Contains(name))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, Mask));
            }
        }
    }
}
