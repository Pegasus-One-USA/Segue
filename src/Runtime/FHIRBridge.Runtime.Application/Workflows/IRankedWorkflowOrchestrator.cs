using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows;

public interface IRankedWorkflowOrchestrator
{
    Task<WorkflowRunResult> ExecuteAsync(
        WorkflowDefinition workflowDefinition,
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Runs only <paramref name="targetNodeId"/>'s ancestor closure (a checkpoint run) when set; the full
    /// graph when null.</summary>
    Task<WorkflowRunResult> ExecuteAsync(
        WorkflowDefinition workflowDefinition,
        WorkflowExecutionContext context,
        Guid? targetNodeId,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a run paused at <paramref name="nodeId"/> (a source node that deferred to an async
    /// bulk-export job) once that job's resources are ready. <paramref name="priorNodeOutputsJson"/> and
    /// <paramref name="contextJson"/> are whatever was persisted onto the <c>BulkExportJob</c> row at pause time.
    /// <paramref name="skippedResourceTypeReasons"/> carries the completed export manifest's <c>error</c> array
    /// (one resource type the server excluded, e.g. not supported/authorized for this client) — surfaced the same
    /// way the REST-search path's per-type authorization skips are, so the run finishes as PartialSuccess rather
    /// than silently missing data.</summary>
    Task<WorkflowRunResult> ResumeAfterBulkExportAsync(
        Guid workflowRunId,
        Guid nodeId,
        string? priorNodeOutputsJson,
        string? contextJson,
        IReadOnlyList<ResourceEnvelope> resources,
        IReadOnlyList<string>? skippedResourceTypeReasons = null,
        CancellationToken cancellationToken = default);
}
