using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Detects anomalies across recent pipeline runs using configurable, statistically-grounded heuristics.
/// Failure, write-ratio and error-rate signals are per-run; latency and throughput-drop signals are evaluated
/// against a baseline computed from the run window (only when enough runs exist to trust the baseline).
/// Thresholds and detector toggles come from <see cref="AnomalyDetectionOptions"/>.
/// </summary>
public sealed class RunAnomalyDetectionService : IAnomalyDetectionService
{
    private readonly IConfiguredPipelineRunRepository _pipelineRunRepository;
    private readonly AnomalyDetectionOptions _options;
    private readonly ISystemSettingsCache _settingsCache;

    public RunAnomalyDetectionService(
        IConfiguredPipelineRunRepository pipelineRunRepository,
        ISystemSettingsCache settingsCache,
        AnomalyDetectionOptions? options = null)
    {
        _pipelineRunRepository = pipelineRunRepository;
        _settingsCache = settingsCache;
        _options = options ?? AnomalyDetectionOptions.Default;
    }

    public async Task<RunAnomalySummaryDto> AnalyzeRunsAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var effective = await ResolveEffectiveOptionsAsync(cancellationToken);
        var take = count <= 0 ? 100 : Math.Min(count, 1000);
        var runs = (await _pipelineRunRepository.GetRecentAsync(take, cancellationToken))
            .OrderBy(run => run.StartedOnUtc)
            .ToList();
        var anomalies = new List<RunAnomalyDto>();

        if (runs.Count == 0)
        {
            return new RunAnomalySummaryDto(DateTime.UtcNow, 0, anomalies);
        }

        var durations = runs
            .Select(run => Math.Max(0, (run.CompletedOnUtc - run.StartedOnUtc).TotalMilliseconds))
            .ToList();
        var averageDuration = durations.Average();
        var durationStdDev = StandardDeviation(durations, averageDuration);

        // Throughput baseline: mean extracted resources over completed runs that actually extracted something.
        var extractionBaseline = runs
            .Where(run => IsCompleted(run) && run.ExtractedResourceCount > 0)
            .Select(run => (double)run.ExtractedResourceCount)
            .DefaultIfEmpty(0)
            .Average();

        var hasBaseline = runs.Count >= effective.MinBaselineRuns;

        foreach (var run in runs)
        {
            var completed = IsCompleted(run);

            if (effective.DetectFailures && !completed)
            {
                anomalies.Add(new RunAnomalyDto(
                    run.Id,
                    "High",
                    "Failure",
                    $"Run ended with status '{run.Status}' and {run.Errors.Count} error(s).",
                    1,
                    run.StartedOnUtc));
            }

            // Write-ratio is meaningful on any run that mapped records, completed or not.
            if (effective.DetectWriteRatio && run.MappedRecordCount > 0 && run.WrittenRecordCount < run.MappedRecordCount)
            {
                var writeRatio = (decimal)run.WrittenRecordCount / run.MappedRecordCount;
                anomalies.Add(new RunAnomalyDto(
                    run.Id,
                    writeRatio == 0 ? "High" : "Medium",
                    "WriteRatio",
                    $"Only {run.WrittenRecordCount} of {run.MappedRecordCount} mapped records were written.",
                    Math.Round(1 - writeRatio, 4),
                    run.StartedOnUtc));
            }

            // The remaining detectors describe a *completed* run's quality, so a failure isn't double-counted.
            if (!completed)
            {
                continue;
            }

            if (effective.DetectErrorRate && run.Errors.Count > effective.ErrorRateWarnThreshold)
            {
                anomalies.Add(new RunAnomalyDto(
                    run.Id,
                    run.Errors.Count > effective.ErrorRateWarnThreshold + 5 ? "Medium" : "Low",
                    "ErrorRate",
                    $"Run completed but logged {run.Errors.Count} error(s).",
                    Math.Min(1, Math.Round((decimal)run.Errors.Count / 10, 4)),
                    run.StartedOnUtc));
            }

            if (effective.DetectZeroExtraction && hasBaseline && extractionBaseline > 0 && run.ExtractedResourceCount == 0)
            {
                anomalies.Add(new RunAnomalyDto(
                    run.Id,
                    "Medium",
                    "ZeroExtraction",
                    $"Run extracted 0 resources while the recent baseline is ~{Math.Round(extractionBaseline)}.",
                    1,
                    run.StartedOnUtc));
            }
            else if (effective.DetectThroughputDrop && hasBaseline && extractionBaseline > 0
                && run.ExtractedResourceCount > 0
                && run.ExtractedResourceCount < extractionBaseline * effective.ThroughputDropFraction)
            {
                var dropRatio = run.ExtractedResourceCount / extractionBaseline;
                anomalies.Add(new RunAnomalyDto(
                    run.Id,
                    "Medium",
                    "ThroughputDrop",
                    $"Run extracted {run.ExtractedResourceCount} resources, far below the recent baseline of ~{Math.Round(extractionBaseline)}.",
                    Math.Round((decimal)(1 - dropRatio), 4),
                    run.StartedOnUtc));
            }

            var duration = Math.Max(0, (run.CompletedOnUtc - run.StartedOnUtc).TotalMilliseconds);
            if (effective.DetectLatency && hasBaseline && durationStdDev > 0
                && duration > averageDuration + durationStdDev * effective.LatencySigmaMultiplier)
            {
                anomalies.Add(new RunAnomalyDto(
                    run.Id,
                    "Medium",
                    "Latency",
                    $"Run duration {Math.Round(duration)} ms is above the historical baseline.",
                    Math.Round((decimal)((duration - averageDuration) / durationStdDev), 4),
                    run.StartedOnUtc));
            }
        }

        return new RunAnomalySummaryDto(
            DateTime.UtcNow,
            runs.Count,
            anomalies
                .OrderByDescending(anomaly => SeverityRank(anomaly.Severity))
                .ThenByDescending(anomaly => anomaly.Score)
                .ToList());
    }

    private async Task<AnomalyDetectionOptions> ResolveEffectiveOptionsAsync(CancellationToken cancellationToken) =>
        new()
        {
            LatencySigmaMultiplier = await _settingsCache.GetDoubleAsync(
                "AnomalyDetection:LatencySigmaMultiplier", _options.LatencySigmaMultiplier, cancellationToken),
            MinBaselineRuns = await _settingsCache.GetIntAsync(
                "AnomalyDetection:MinBaselineRuns", _options.MinBaselineRuns, cancellationToken),
            ThroughputDropFraction = await _settingsCache.GetDoubleAsync(
                "AnomalyDetection:ThroughputDropFraction", _options.ThroughputDropFraction, cancellationToken),
            ErrorRateWarnThreshold = await _settingsCache.GetIntAsync(
                "AnomalyDetection:ErrorRateWarnThreshold", _options.ErrorRateWarnThreshold, cancellationToken),
            DetectFailures = await _settingsCache.GetBoolAsync(
                "AnomalyDetection:DetectFailures", _options.DetectFailures, cancellationToken),
            DetectWriteRatio = await _settingsCache.GetBoolAsync(
                "AnomalyDetection:DetectWriteRatio", _options.DetectWriteRatio, cancellationToken),
            DetectLatency = await _settingsCache.GetBoolAsync(
                "AnomalyDetection:DetectLatency", _options.DetectLatency, cancellationToken),
            DetectZeroExtraction = await _settingsCache.GetBoolAsync(
                "AnomalyDetection:DetectZeroExtraction", _options.DetectZeroExtraction, cancellationToken),
            DetectThroughputDrop = await _settingsCache.GetBoolAsync(
                "AnomalyDetection:DetectThroughputDrop", _options.DetectThroughputDrop, cancellationToken),
            DetectErrorRate = await _settingsCache.GetBoolAsync(
                "AnomalyDetection:DetectErrorRate", _options.DetectErrorRate, cancellationToken),
        };

    private static bool IsCompleted(ConfiguredPipelineRunDto run)
        => string.Equals(run.Status, "Completed", StringComparison.OrdinalIgnoreCase);

    private static int SeverityRank(string severity) => severity switch
    {
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0
    };

    private static double StandardDeviation(IReadOnlyCollection<double> values, double average)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var variance = values.Sum(value => Math.Pow(value - average, 2)) / values.Count;
        return Math.Sqrt(variance);
    }
}
