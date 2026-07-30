namespace FHIRBridge.Api.Workflows;

/// <summary>
/// One row of the (Runtime Plane) Execution History screen — a workflow run joined with its workflow's name and
/// first source connection, shaped to match the Configured Pipeline's <c>PipelineRunRouteExecutionDto</c> so the
/// portal can render both with the same table.
/// </summary>
public sealed record WorkflowRunHistoryDto(
    Guid Id,
    Guid WorkflowDefinitionId,
    string PipelineName,
    string? SourceName,
    string? SourceSystemType,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? TriggeredBy,
    string? TriggerType,
    int NodeRunCount,
    string? ErrorMessage,
    int WorkflowDefinitionVersion,
    string? CorrelationId)
{
    public long? DurationMs => CompletedAt.HasValue
        ? (long)(CompletedAt.Value - StartedAt).TotalMilliseconds
        : null;
}

/// <summary>All-time run count per <see cref="FHIRBridge.Runtime.Domain.Workflows.WorkflowRunStatus"/>, across
/// every workflow definition — backs the Dashboard's status stat tiles.</summary>
public sealed record WorkflowRunStatusCountsDto(
    int Pending,
    int Running,
    int Succeeded,
    int Failed,
    int Cancelled);
