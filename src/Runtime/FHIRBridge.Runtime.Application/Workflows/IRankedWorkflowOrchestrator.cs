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
}
