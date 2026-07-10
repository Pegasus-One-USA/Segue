namespace FHIRBridge.Application.DTOs;

public sealed record PipelineRunRouteExecutionDto(
    Guid Id,
    Guid PipelineRunId,
    string PipelineName,
    string SourceName,
    string SourceSystemType,
    string Status,
    DateTime StartedOnUtc,
    DateTime? CompletedOnUtc,
    string? TriggeredBy,
    string? TriggerType,
    int ExtractedCount,
    int MappedCount,
    int WrittenCount,
    string? ErrorMessage)
{
    /// <summary>Derived, not stored — null while the execution is still running.</summary>
    public long? DurationMs => CompletedOnUtc.HasValue
        ? (long)(CompletedOnUtc.Value - StartedOnUtc).TotalMilliseconds
        : null;
}
