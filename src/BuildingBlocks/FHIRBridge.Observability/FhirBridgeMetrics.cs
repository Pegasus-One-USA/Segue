using System.Diagnostics.Metrics;
using FHIRBridge.SharedKernel.Observability;

namespace FHIRBridge.Observability;

/// <summary>
/// The custom FHIRBridge metrics source. Publishes pipeline counters/histograms through a <see cref="Meter"/> (scraped
/// by OpenTelemetry) and, in parallel, maintains a thread-safe in-process accumulator that backs the admin dashboard
/// (<see cref="IMetricsSnapshotProvider"/>) so operators get throughput/latency without a separate metrics backend.
/// </summary>
public sealed class FhirBridgeMetrics : IPipelineMetrics, IMetricsSnapshotProvider, IDisposable
{
    public const string MeterName = "FHIRBridge.Pipeline";

    private readonly Meter _meter;
    private readonly Counter<long> _runsCounter;
    private readonly Counter<long> _extractedCounter;
    private readonly Counter<long> _mappedCounter;
    private readonly Counter<long> _writtenCounter;
    private readonly Counter<long> _errorsCounter;
    private readonly Histogram<double> _runDuration;

    private readonly int _bufferSize;
    private readonly object _gate = new();
    private readonly LinkedList<RecentRunMetric> _recentRuns = new();

    private long _totalRuns;
    private long _completedRuns;
    private long _completedWithErrorsRuns;
    private long _failedRuns;
    private long _totalExtracted;
    private long _totalMapped;
    private long _totalWritten;
    private long _totalErrors;
    private double _totalDurationMs;
    private double _maxDurationMs;
    private DateTime? _lastRunCompletedOnUtc;

    public FhirBridgeMetrics(int recentRunBufferSize = 100)
    {
        _bufferSize = recentRunBufferSize < 1 ? 1 : recentRunBufferSize;
        _meter = new Meter(MeterName);
        _runsCounter = _meter.CreateCounter<long>(
            "fhirbridge.pipeline.runs", unit: "{run}", description: "Pipeline runs completed, tagged by status.");
        _extractedCounter = _meter.CreateCounter<long>(
            "fhirbridge.pipeline.resources_extracted", unit: "{resource}", description: "FHIR resources extracted from sources.");
        _mappedCounter = _meter.CreateCounter<long>(
            "fhirbridge.pipeline.records_mapped", unit: "{record}", description: "Records produced by the mapping engine.");
        _writtenCounter = _meter.CreateCounter<long>(
            "fhirbridge.pipeline.records_written", unit: "{record}", description: "Records written to destinations.");
        _errorsCounter = _meter.CreateCounter<long>(
            "fhirbridge.pipeline.errors", unit: "{error}", description: "Errors collected during pipeline runs.");
        _runDuration = _meter.CreateHistogram<double>(
            "fhirbridge.pipeline.run_duration", unit: "ms", description: "Wall-clock duration of a pipeline run.");
    }

    public void RecordRun(PipelineRunMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        var status = string.IsNullOrWhiteSpace(metric.Status) ? "Unknown" : metric.Status;
        var statusTag = new KeyValuePair<string, object?>("status", status);
        var durationMs = metric.Duration.TotalMilliseconds;

        // OpenTelemetry instruments.
        _runsCounter.Add(1, statusTag);
        _extractedCounter.Add(metric.ExtractedResourceCount, statusTag);
        _mappedCounter.Add(metric.MappedRecordCount, statusTag);
        _writtenCounter.Add(metric.WrittenRecordCount, statusTag);
        if (metric.ErrorCount > 0)
        {
            _errorsCounter.Add(metric.ErrorCount, statusTag);
        }

        _runDuration.Record(durationMs, statusTag);

        // In-process accumulator for the admin dashboard.
        lock (_gate)
        {
            _totalRuns++;
            switch (status)
            {
                case "Completed":
                    _completedRuns++;
                    break;
                case "CompletedWithErrors":
                    _completedWithErrorsRuns++;
                    break;
                case "Failed":
                    _failedRuns++;
                    break;
            }

            _totalExtracted += metric.ExtractedResourceCount;
            _totalMapped += metric.MappedRecordCount;
            _totalWritten += metric.WrittenRecordCount;
            _totalErrors += metric.ErrorCount;
            _totalDurationMs += durationMs;
            if (durationMs > _maxDurationMs)
            {
                _maxDurationMs = durationMs;
            }

            _lastRunCompletedOnUtc = metric.CompletedOnUtc;

            _recentRuns.AddFirst(new RecentRunMetric(
                status,
                metric.ExtractedResourceCount,
                metric.MappedRecordCount,
                metric.WrittenRecordCount,
                metric.ErrorCount,
                durationMs,
                metric.CompletedOnUtc));

            while (_recentRuns.Count > _bufferSize)
            {
                _recentRuns.RemoveLast();
            }
        }
    }

    public MetricsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var recent = _recentRuns.ToList();
            var averageMs = _totalRuns == 0 ? 0 : _totalDurationMs / _totalRuns;
            var p95Ms = ComputeP95(recent);

            // Throughput estimate: written records per hour, derived from observed run durations. Uses the recent
            // window when available so it tracks current load rather than lifetime totals.
            var window = recent.Count > 0 ? recent : null;
            double estimatedPerHour;
            if (window is not null)
            {
                var windowDurationMs = window.Sum(run => run.DurationMs);
                var windowWritten = window.Sum(run => (long)run.WrittenRecordCount);
                estimatedPerHour = windowDurationMs > 0
                    ? windowWritten / (windowDurationMs / 3_600_000d)
                    : 0;
            }
            else
            {
                estimatedPerHour = 0;
            }

            return new MetricsSnapshot(
                _totalRuns,
                _completedRuns,
                _completedWithErrorsRuns,
                _failedRuns,
                _totalExtracted,
                _totalMapped,
                _totalWritten,
                _totalErrors,
                Math.Round(averageMs, 2),
                Math.Round(p95Ms, 2),
                Math.Round(_maxDurationMs, 2),
                Math.Round(estimatedPerHour, 2),
                _lastRunCompletedOnUtc,
                recent);
        }
    }

    private static double ComputeP95(IReadOnlyList<RecentRunMetric> runs)
    {
        if (runs.Count == 0)
        {
            return 0;
        }

        var ordered = runs.Select(run => run.DurationMs).OrderBy(value => value).ToArray();
        // Nearest-rank method on the retained recent window.
        var rank = (int)Math.Ceiling(0.95 * ordered.Length) - 1;
        rank = Math.Clamp(rank, 0, ordered.Length - 1);
        return ordered[rank];
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}
