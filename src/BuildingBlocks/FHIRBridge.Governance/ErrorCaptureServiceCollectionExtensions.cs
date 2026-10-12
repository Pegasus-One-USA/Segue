using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FHIRBridge.Governance;

public static class ErrorCaptureServiceCollectionExtensions
{
    /// <summary>
    /// Adds the central error-capture layer: the shared PHI scrubber, the configurable sinks (table / Application
    /// Insights / both) and the ambient catch-all. Additive — <see cref="IGlobalExceptionManager"/> picks these up
    /// automatically (they are optional constructor dependencies), and with no <c>ErrorCapture</c> configuration the
    /// behaviour is the original table-only capture. Call from every host (Api, Worker).
    /// </summary>
    public static IServiceCollection AddFhirBridgeErrorCapture(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ErrorCaptureOptions>(configuration.GetSection(ErrorCaptureOptions.SectionName));
        ErrorScrubber.ConfigureTokenKey(configuration[ErrorCaptureOptions.SectionName + ":ResourceIdTokenKey"]);
        services.PostConfigure<ErrorCaptureOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.ApplicationInsights.ConnectionString))
            {
                options.ApplicationInsights.ConnectionString =
                    FirstNonEmpty(
                        configuration["ApplicationInsights:ConnectionString"],
                        configuration["Observability:AzureMonitorConnectionString"]);
            }
        });

        services.TryAddSingleton<IPhiRedactor>(sp => new PhiRedactor(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<IErrorScrubber, ErrorScrubber>();
        // Replaced by the database-backed policy when the Infrastructure layer is registered.
        services.TryAddSingleton<IErrorCapturePolicy, DefaultErrorCapturePolicy>();
        services.TryAddSingleton<ApplicationInsightsErrorSink>();
        services.TryAddScoped<IErrorSinkRouter, ErrorSinkRouter>();
        services.AddHostedService<AmbientErrorCaptureService>();
        // Errors are written by a background service, so recording one never blocks (or fails) the code that hit it.
        services.TryAddSingleton<IErrorWriteQueue, ErrorWriteQueue>();
        services.AddHostedService<ErrorWriteService>();

        return services;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
