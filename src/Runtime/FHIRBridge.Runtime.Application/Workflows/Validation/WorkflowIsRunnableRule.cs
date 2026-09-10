using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Validation;

/// <summary>
/// The workflow itself must be in a state that can run at all — enabled, and with a source and a destination to
/// run between. Caught here rather than at execution so a disabled or half-built workflow is reported as a
/// validation refusal (with an Execution History row explaining it) instead of an opaque runtime failure.
/// </summary>
public sealed class WorkflowIsRunnableRule : IWorkflowRunParameterRule
{
    public Task<IReadOnlyList<WorkflowRunValidationError>> ValidateAsync(
        WorkflowRunValidationContext context,
        CancellationToken cancellationToken)
    {
        var errors = new List<WorkflowRunValidationError>();

        if (!context.Workflow.IsEnabled)
        {
            errors.Add(new WorkflowRunValidationError(
                "workflowId", "This workflow is disabled and cannot be run."));
        }

        var enabledNodes = context.Workflow.Nodes.Where(node => node.IsEnabled).ToArray();

        if (!enabledNodes.Any(node => node.Category == WorkflowNodeCategory.Source))
        {
            errors.Add(new WorkflowRunValidationError(
                "workflowId", "This workflow has no enabled source node, so there is nothing to read from."));
        }

        if (!enabledNodes.Any(node => node.Category == WorkflowNodeCategory.Destination))
        {
            errors.Add(new WorkflowRunValidationError(
                "workflowId", "This workflow has no enabled destination node, so there is nowhere to write to."));
        }

        return Task.FromResult<IReadOnlyList<WorkflowRunValidationError>>(errors);
    }
}
