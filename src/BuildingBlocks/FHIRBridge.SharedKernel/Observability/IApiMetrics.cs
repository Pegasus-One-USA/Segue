namespace FHIRBridge.SharedKernel.Observability;

/// <summary>
/// Records one completed outbound HTTP call for observability — mirrors <see cref="IPipelineMetrics"/>'s split
/// between OpenTelemetry export and an in-process snapshot for the portal, but for API Analytics instead of
/// pipeline throughput.
/// </summary>
public interface IApiMetrics
{
    void RecordRequest(ApiRequestMetric metric);
}

/// <summary>A single completed outbound HTTP call. PHI-free — method/URL-without-query/status/duration only.</summary>
public sealed record ApiRequestMetric(
    string Method,
    string Url,
    int? StatusCode,
    long DurationMs);

/// <summary>
/// Exposes an in-process, point-in-time view of outbound API call metrics for the current host process — the
/// same "this instance only, OTLP backend does cross-instance rollup" model as <see cref="IMetricsSnapshotProvider"/>.
/// </summary>
public interface IApiMetricsSnapshotProvider
{
    ApiMetricsSnapshot GetSnapshot();
}

public sealed record ApiMetricsSnapshot(
    long TotalRequests,
    long TotalErrors,
    double ErrorRatePercent,
    IReadOnlyList<ApiEndpointMetric> TopByCallCount,
    IReadOnlyList<ApiEndpointMetric> SlowestByAverageDuration);

/// <summary>Aggregated stats for one method+URL pair within the current process's lifetime (bounded, see the provider).</summary>
public sealed record ApiEndpointMetric(
    string Method,
    string Url,
    long CallCount,
    double AverageDurationMs,
    double P95DurationMs,
    long ErrorCount);
