using System.Diagnostics.Metrics;
using FHIRBridge.SharedKernel.Observability;

namespace FHIRBridge.Observability;

/// <summary>
/// Publishes outbound-HTTP-call counters/histograms through a <see cref="Meter"/> (scraped by OpenTelemetry) and,
/// in parallel, maintains a thread-safe in-process accumulator that backs the API Analytics screen
/// (<see cref="IApiMetricsSnapshotProvider"/>) — the same split <see cref="FhirBridgeMetrics"/> uses for pipeline runs.
/// </summary>
public sealed class FhirBridgeApiMetrics : IApiMetrics, IApiMetricsSnapshotProvider, IDisposable
{
    public const string MeterName = "FHIRBridge.Api";
    private const int RecentDurationsPerEndpoint = 200;

    private readonly Meter _meter;
    private readonly Counter<long> _requestsCounter;
    private readonly Histogram<double> _requestDuration;

    private readonly object _gate = new();
    private readonly Dictionary<string, EndpointAccumulator> _byEndpoint = new();
    private long _totalRequests;
    private long _totalErrors;

    public FhirBridgeApiMetrics()
    {
        _meter = new Meter(MeterName);
        _requestsCounter = _meter.CreateCounter<long>(
            "fhirbridge.api.requests", unit: "{request}", description: "Outbound HTTP calls, tagged by method/status.");
        _requestDuration = _meter.CreateHistogram<double>(
            "fhirbridge.api.request_duration", unit: "ms", description: "Outbound HTTP call duration.");
    }

    public void RecordRequest(ApiRequestMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        var isError = metric.StatusCode is null or >= 400;
        var tags = new KeyValuePair<string, object?>[]
        {
            new("method", metric.Method),
            new("status", metric.StatusCode?.ToString() ?? "error"),
        };

        _requestsCounter.Add(1, tags);
        _requestDuration.Record(metric.DurationMs, tags);

        var key = $"{metric.Method} {metric.Url}";
        lock (_gate)
        {
            _totalRequests++;
            if (isError)
            {
                _totalErrors++;
            }

            if (!_byEndpoint.TryGetValue(key, out var accumulator))
            {
                accumulator = new EndpointAccumulator(metric.Method, metric.Url);
                _byEndpoint[key] = accumulator;
            }

            accumulator.Record(metric.DurationMs, isError);
        }
    }

    public ApiMetricsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var endpoints = _byEndpoint.Values.Select(a => a.ToMetric()).ToList();
            var errorRate = _totalRequests == 0 ? 0 : _totalErrors * 100.0 / _totalRequests;

            return new ApiMetricsSnapshot(
                _totalRequests,
                _totalErrors,
                Math.Round(errorRate, 2),
                endpoints.OrderByDescending(e => e.CallCount).Take(10).ToList(),
                endpoints.OrderByDescending(e => e.AverageDurationMs).Take(10).ToList());
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    private sealed class EndpointAccumulator
    {
        private readonly string _method;
        private readonly string _url;
        private readonly Queue<double> _recentDurations = new();
        private long _count;
        private long _errorCount;
        private double _totalDurationMs;

        public EndpointAccumulator(string method, string url)
        {
            _method = method;
            _url = url;
        }

        public void Record(double durationMs, bool isError)
        {
            _count++;
            _totalDurationMs += durationMs;
            if (isError)
            {
                _errorCount++;
            }

            _recentDurations.Enqueue(durationMs);
            while (_recentDurations.Count > RecentDurationsPerEndpoint)
            {
                _recentDurations.Dequeue();
            }
        }

        public ApiEndpointMetric ToMetric()
        {
            var ordered = _recentDurations.OrderBy(d => d).ToArray();
            var p95 = ordered.Length == 0 ? 0 : ordered[Math.Clamp((int)Math.Ceiling(0.95 * ordered.Length) - 1, 0, ordered.Length - 1)];

            return new ApiEndpointMetric(
                _method,
                _url,
                _count,
                _count == 0 ? 0 : Math.Round(_totalDurationMs / _count, 2),
                Math.Round(p95, 2),
                _errorCount);
        }
    }
}
