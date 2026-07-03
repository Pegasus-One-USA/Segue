using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.SharedKernel.Observability;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Builds the observability dashboard snapshot from the durable <c>ConfiguredPipelineRuns</c> store rather than a
/// per-process in-memory accumulator. Because the run store is shared, the snapshot reflects runs executed in ANY
/// host (API or Worker) — fixing the per-process blind spot where Worker-executed scheduled runs never appeared on
/// the API-served dashboard.
/// </summary>
public interface IPipelineRunMetricsService
{
    Task<MetricsSnapshot> GetProcessWideSnapshotAsync(int windowSize, CancellationToken cancellationToken);
}

public sealed class PipelineRunMetricsService : IPipelineRunMetricsService
{
    private const int RecentRunsReturned = 100;

    private readonly IConfiguredPipelineRunRepository _runRepository;

    public PipelineRunMetricsService(IConfiguredPipelineRunRepository runRepository)
    {
        _runRepository = runRepository;
    }

    public async Task<MetricsSnapshot> GetProcessWideSnapshotAsync(int windowSize, CancellationToken cancellationToken)
    {
        var window = windowSize <= 0 ? 200 : windowSize;
        var runs = await _runRepository.GetRecentAsync(window, cancellationToken);

        if (runs.Count == 0)
        {
            return new MetricsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, []);
        }

        long completed = 0, completedWithErrors = 0, failed = 0;
        long totalExtracted = 0, totalMapped = 0, totalWritten = 0, totalErrors = 0;
        double totalDurationMs = 0, maxDurationMs = 0, windowWritten = 0, windowDurationMs = 0;
        DateTime? lastCompleted = null;
        var durations = new List<double>(runs.Count);
        var recent = new List<RecentRunMetric>(Math.Min(runs.Count, RecentRunsReturned));

        foreach (var run in runs)
        {
            var status = Classify(run);
            switch (status)
            {
                case "Completed": completed++; break;
                case "CompletedWithErrors": completedWithErrors++; break;
                case "Failed": failed++; break;
            }

            var durationMs = Math.Max(0, (run.CompletedOnUtc - run.StartedOnUtc).TotalMilliseconds);
            totalExtracted += run.ExtractedResourceCount;
            totalMapped += run.MappedRecordCount;
            totalWritten += run.WrittenRecordCount;
            totalErrors += run.Errors.Count;
            totalDurationMs += durationMs;
            windowDurationMs += durationMs;
            windowWritten += run.WrittenRecordCount;
            maxDurationMs = Math.Max(maxDurationMs, durationMs);
            durations.Add(durationMs);

            if (lastCompleted is null || run.CompletedOnUtc > lastCompleted)
            {
                lastCompleted = run.CompletedOnUtc;
            }

            if (recent.Count < RecentRunsReturned)
            {
                recent.Add(new RecentRunMetric(
                    status, run.ExtractedResourceCount, run.MappedRecordCount,
                    run.WrittenRecordCount, run.Errors.Count, durationMs, run.CompletedOnUtc));
            }
        }

        var averageMs = totalDurationMs / runs.Count;
        var estimatedPerHour = windowDurationMs > 0 ? windowWritten / (windowDurationMs / 3_600_000d) : 0;

        return new MetricsSnapshot(
            runs.Count,
            completed,
            completedWithErrors,
            failed,
            totalExtracted,
            totalMapped,
            totalWritten,
            totalErrors,
            Math.Round(averageMs, 2),
            Math.Round(ComputeP95(durations), 2),
            Math.Round(maxDurationMs, 2),
            Math.Round(estimatedPerHour, 2),
            lastCompleted,
            recent);
    }

    private static string Classify(ConfiguredPipelineRunDto run)
    {
        if (string.Equals(run.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Failed";
        }

        if (string.Equals(run.Status, "CompletedWithErrors", StringComparison.OrdinalIgnoreCase))
        {
            return "CompletedWithErrors";
        }

        if (string.Equals(run.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return run.Errors.Count > 0 ? "CompletedWithErrors" : "Completed";
        }

        return run.Status;
    }

    private static double ComputeP95(List<double> durations)
    {
        if (durations.Count == 0)
        {
            return 0;
        }

        durations.Sort();
        var rank = (int)Math.Ceiling(0.95 * durations.Count) - 1;
        rank = Math.Clamp(rank, 0, durations.Count - 1);
        return durations[rank];
    }
}
