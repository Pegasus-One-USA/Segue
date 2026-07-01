namespace FHIRBridge.Observability;

/// <summary>
/// Binds the "Observability" configuration section. Controls OpenTelemetry export and the in-process dashboard buffer.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Master switch. When false the OpenTelemetry pipeline is not registered (custom metrics still record).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// OTLP exporter endpoint (e.g. http://otel-collector:4317). When empty no exporter is added, so traces/metrics are
    /// only kept in-process for the admin dashboard. Set in production to ship to a collector / APM backend.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// Azure Monitor/Application Insights connection string. When populated, traces and metrics are exported directly
    /// to Azure Monitor in addition to any configured OTLP collector.
    /// </summary>
    public string? AzureMonitorConnectionString { get; set; }

    /// <summary>Emit ASP.NET Core + outbound HTTP traces.</summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>Emit ASP.NET Core, HTTP, .NET runtime and custom FHIRBridge meters.</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>How many recent runs the in-process dashboard buffer retains.</summary>
    public int RecentRunBufferSize { get; set; } = 100;
}
