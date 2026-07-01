using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows;

public interface IRankedWorkflowOrchestrator
{
    Task<WorkflowRunResult> ExecuteAsync(
        WorkflowDefinition workflowDefinition,
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default);
}
