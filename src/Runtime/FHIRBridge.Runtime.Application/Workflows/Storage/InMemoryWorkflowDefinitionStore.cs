using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

public sealed class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly Dictionary<Guid, WorkflowDefinition> _workflows = [];

    public Task<WorkflowDefinition> SaveAsync(WorkflowDefinition workflowDefinition, CancellationToken cancellationToken)
    {
        _workflows[workflowDefinition.Id] = workflowDefinition;
        return Task.FromResult(workflowDefinition);
    }

    public Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<WorkflowDefinition> workflows = _workflows.Values
            .OrderBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(workflows);
    }

    public Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        _workflows.TryGetValue(workflowId, out var workflowDefinition);
        return Task.FromResult(workflowDefinition);
    }

    public Task DeleteAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        _workflows.Remove(workflowId);
        return Task.CompletedTask;
    }
}
