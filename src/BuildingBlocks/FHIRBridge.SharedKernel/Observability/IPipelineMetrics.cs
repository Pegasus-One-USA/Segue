namespace FHIRBridge.SharedKernel.Observability;

/// <summary>
/// Records the outcome of a completed pipeline run for observability. Implemented by the Observability building block
/// (OpenTelemetry meters + an in-process snapshot); injected into the pipeline as an optional dependency so the
/// application/infrastructure layers stay decoupled from OpenTelemetry and unit tests can omit it entirely.
/// </summary>
public interface IPipelineMetrics
{
    void RecordRun(PipelineRunMetric metric);
}

/// <summary>
/// A single completed pipeline run, captured at completion time. PHI-free by construction — counts and timing only.
/// </summary>
/// <param name="Status">Terminal status (Completed, CompletedWithErrors, Failed).</param>
/// <param name="ExtractedResourceCount">FHIR resources read from the source(s).</param>
/// <param name="MappedRecordCount">Records produced by the mapping engine.</param>
/// <param name="WrittenRecordCount">Records written to the destination(s).</param>
/// <param name="ErrorCount">Number of errors collected during the run.</param>
/// <param name="Duration">Wall-clock duration of the run.</param>
/// <param name="CompletedOnUtc">When the run completed (UTC).</param>
public sealed record PipelineRunMetric(
    string Status,
    int ExtractedResourceCount,
    int MappedRecordCount,
    int WrittenRecordCount,
    int ErrorCount,
    TimeSpan Duration,
    DateTime CompletedOnUtc);
