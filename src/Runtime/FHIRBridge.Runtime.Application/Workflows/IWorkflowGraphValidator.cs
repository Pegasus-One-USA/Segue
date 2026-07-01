using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows;

public interface IWorkflowGraphValidator
{
    WorkflowGraphValidationResult Validate(WorkflowDefinition workflowDefinition);
}
