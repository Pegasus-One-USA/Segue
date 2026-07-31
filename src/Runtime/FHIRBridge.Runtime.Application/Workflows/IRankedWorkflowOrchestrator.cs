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
    /// <paramref name="contextJson"/> are whatever was persisted onto the <c>BulkExportJob</c> row at pause time.</summary>
    Task<WorkflowRunResult> ResumeAfterBulkExportAsync(
        Guid workflowRunId,
        Guid nodeId,
        string? priorNodeOutputsJson,
        string? contextJson,
        IReadOnlyList<ResourceEnvelope> resources,
        CancellationToken cancellationToken = default);
}
