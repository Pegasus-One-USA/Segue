namespace FHIRBridge.Application.Services;

/// <summary>
/// Tunable thresholds for <see cref="RunAnomalyDetectionService"/>. Bound from the "AnomalyDetection" configuration
/// section by the host; when unbound the platform defaults below apply (backward-compatible). Each detector can be
/// toggled independently so operators can suppress noisy signals without code changes.
/// </summary>
public sealed class AnomalyDetectionOptions
{
    public const string SectionName = "AnomalyDetection";

    /// <summary>Standard-deviation multiplier above the mean run duration that flags a latency outlier.</summary>
    public double LatencySigmaMultiplier { get; set; } = 3.0;

    /// <summary>Minimum number of runs required before a statistical baseline (latency/throughput) is trusted.</summary>
    public int MinBaselineRuns { get; set; } = 5;

    /// <summary>
    /// Fraction of the recent extraction baseline below which a completed run's extraction count is flagged as a
    /// throughput drop (e.g. 0.2 = flag when a run extracts less than 20% of the recent average).
    /// </summary>
    public double ThroughputDropFraction { get; set; } = 0.2;

    /// <summary>Number of logged errors on an otherwise-completed run above which an error-rate anomaly is raised.</summary>
    public int ErrorRateWarnThreshold { get; set; } = 0;

    public bool DetectFailures { get; set; } = true;
    public bool DetectWriteRatio { get; set; } = true;
    public bool DetectLatency { get; set; } = true;
    public bool DetectZeroExtraction { get; set; } = true;
    public bool DetectThroughputDrop { get; set; } = true;
    public bool DetectErrorRate { get; set; } = true;

    public static AnomalyDetectionOptions Default { get; } = new();
}
