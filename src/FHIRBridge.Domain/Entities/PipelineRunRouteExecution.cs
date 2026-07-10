using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>Lifecycle states for a single route's execution within a pipeline run.</summary>
public static class PipelineRunRouteExecutionStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string CompletedWithErrors = "CompletedWithErrors";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>
/// One row per route processed within a <see cref="ConfiguredPipelineRunRecord"/> — the display grain the
/// Execution History screen renders (one named pipeline/source/status/duration per row). The parent run record
/// remains the aggregate (used for incremental-sync watermarking); this is the durable, queryable per-route detail.
/// Name/source fields are snapshotted at execution time so history reads correctly even if the mapping profile or
/// source connection is later renamed.
/// </summary>
public sealed class PipelineRunRouteExecution : Entity<Guid>
{
    private PipelineRunRouteExecution()
    {
    }

    public PipelineRunRouteExecution(
        Guid id,
        Guid pipelineRunId,
        Guid routeId,
        Guid mappingProfileId,
        string pipelineName,
        Guid sourceConnectionId,
        string sourceName,
        string sourceSystemType,
        string? triggeredBy,
        string? triggerType,
        DateTime startedOnUtc)
    {
        Id = id;
        PipelineRunId = pipelineRunId;
        RouteId = routeId;
        MappingProfileId = mappingProfileId;
        PipelineName = pipelineName;
        SourceConnectionId = sourceConnectionId;
        SourceName = sourceName;
        SourceSystemType = sourceSystemType;
        TriggeredBy = triggeredBy;
        TriggerType = triggerType;
        StartedOnUtc = startedOnUtc;
        Status = PipelineRunRouteExecutionStatus.Running;
    }

    public Guid PipelineRunId { get; private set; }
    public Guid RouteId { get; private set; }
    public Guid MappingProfileId { get; private set; }
    public string PipelineName { get; private set; } = default!;
    public Guid SourceConnectionId { get; private set; }
    public string SourceName { get; private set; } = default!;
    public string SourceSystemType { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public string? TriggeredBy { get; private set; }
    public string? TriggerType { get; private set; }
    public int ExtractedCount { get; private set; }
    public int MappedCount { get; private set; }
    public int WrittenCount { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void Complete(
        string status,
        int extractedCount,
        int mappedCount,
        int writtenCount,
        string? errorMessage,
        DateTime completedOnUtc)
    {
        Status = status;
        ExtractedCount = extractedCount;
        MappedCount = mappedCount;
        WrittenCount = writtenCount;
        ErrorMessage = errorMessage;
        CompletedOnUtc = completedOnUtc;
    }
}
