using FHIRBridge.SharedKernel.Observability;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FHIRBridge.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers FHIRBridge observability: custom pipeline metrics (<see cref="IPipelineMetrics"/> +
    /// <see cref="IMetricsSnapshotProvider"/>) and, unless disabled, the OpenTelemetry tracing/metrics pipeline
    /// (ASP.NET Core + HTTP + .NET runtime instrumentation and the custom FHIRBridge meter, with an optional OTLP
    /// exporter). Safe to call from any host; idempotent registration of the singleton metrics source.
    /// </summary>
    public static IServiceCollection AddFhirBridgeObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);

        services.Configure<ObservabilityOptions>(configuration.GetSection(ObservabilityOptions.SectionName));

        // The custom metrics source is a singleton shared as both the recorder and the dashboard snapshot provider.
        var metrics = new FhirBridgeMetrics(options.RecentRunBufferSize);
        services.AddSingleton(metrics);
        services.AddSingleton<IPipelineMetrics>(metrics);
        services.AddSingleton<IMetricsSnapshotProvider>(metrics);

        // Same split, for outbound API calls (API Analytics) instead of pipeline runs.
        var apiMetrics = new FhirBridgeApiMetrics();
        services.AddSingleton(apiMetrics);
        services.AddSingleton<IApiMetrics>(apiMetrics);
        services.AddSingleton<IApiMetricsSnapshotProvider>(apiMetrics);

        // Live CPU/memory for the current process — backs System Health's "this process" row. No OTel export of
        // its own; it's a point-in-time diagnostic read, not a time-series metric worth shipping to a backend.
        services.AddSingleton<IProcessHealthProvider, ProcessHealthProvider>();

        if (!options.Enabled)
        {
            return services;
        }

        var resourceBuilder = ResourceBuilder.CreateDefault().AddService(serviceName);
        var azureMonitorConnectionString = string.IsNullOrWhiteSpace(options.AzureMonitorConnectionString)
            ? configuration["ApplicationInsights:ConnectionString"]
            : options.AzureMonitorConnectionString;

        var otel = services.AddOpenTelemetry();

        if (options.EnableTracing)
        {
            otel.WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(FhirBridgeActivitySource.Name)
                    // Best-effort: the Azure.Messaging.ServiceBus SDK has built-in Activity sources for
                    // send/process that populate W3C trace headers on the wire once something is listening — a
                    // no-op for hosts not using the Azure Service Bus transport. RabbitMQ / in-memory
                    // IMessageConsumer implementations have no equivalent propagation today.
                    .AddSource("Azure.*");

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
                }

                if (!string.IsNullOrWhiteSpace(azureMonitorConnectionString))
                {
                    tracing.AddAzureMonitorTraceExporter(exporter =>
                    {
                        exporter.ConnectionString = azureMonitorConnectionString;
                    });
                }
            });
        }

        if (options.EnableMetrics)
        {
            otel.WithMetrics(meterProvider =>
            {
                meterProvider
                    .SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(FhirBridgeMetrics.MeterName)
                    .AddMeter(FhirBridgeApiMetrics.MeterName);

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    meterProvider.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
                }

                if (!string.IsNullOrWhiteSpace(azureMonitorConnectionString))
                {
                    meterProvider.AddAzureMonitorMetricExporter(exporter =>
                    {
                        exporter.ConnectionString = azureMonitorConnectionString;
                    });
                }
            });
        }

        return services;
    }
}
