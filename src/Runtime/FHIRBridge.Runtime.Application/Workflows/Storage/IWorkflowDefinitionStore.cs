using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

public interface IWorkflowDefinitionStore
{
    Task<WorkflowDefinition> SaveAsync(WorkflowDefinition workflowDefinition, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken);

    Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken);
}
