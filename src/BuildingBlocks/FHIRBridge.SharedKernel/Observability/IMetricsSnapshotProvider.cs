namespace FHIRBridge.SharedKernel.Observability;

/// <summary>
/// Exposes an in-process, point-in-time view of the pipeline metrics for the admin observability dashboard. The
/// snapshot covers runs executed within this host process; cross-process aggregation is done by an OTLP backend.
/// </summary>
public interface IMetricsSnapshotProvider
{
    MetricsSnapshot GetSnapshot();
}

/// <summary>
/// Aggregated pipeline throughput and latency for the current host process. Drives the admin dashboard.
/// </summary>
public sealed record MetricsSnapshot(
    long TotalRuns,
    long CompletedRuns,
    long CompletedWithErrorsRuns,
    long FailedRuns,
    long TotalResourcesExtracted,
    long TotalRecordsMapped,
    long TotalRecordsWritten,
    long TotalErrors,
    double AverageRunDurationMs,
    double P95RunDurationMs,
    double MaxRunDurationMs,
    double EstimatedRecordsPerHour,
    DateTime? LastRunCompletedOnUtc,
    IReadOnlyList<RecentRunMetric> RecentRuns);

/// <summary>
/// A recent run summary for the dashboard activity table. PHI-free — counts, status and timing only.
/// </summary>
public sealed record RecentRunMetric(
    string Status,
    int ExtractedResourceCount,
    int MappedRecordCount,
    int WrittenRecordCount,
    int ErrorCount,
    double DurationMs,
    DateTime CompletedOnUtc);
