namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowNodeExecutorRegistry : IWorkflowNodeExecutorRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkflowNodeExecutor> _executorsByNodeType;

    public WorkflowNodeExecutorRegistry(IEnumerable<IWorkflowNodeExecutor> executors)
    {
        _executorsByNodeType = executors.ToDictionary(
            executor => executor.NodeType,
            StringComparer.OrdinalIgnoreCase);
    }

    public IWorkflowNodeExecutor? Get(string nodeType)
        => _executorsByNodeType.TryGetValue(nodeType, out var executor) ? executor : null;

    public IWorkflowNodeExecutor GetRequired(string nodeType)
    {
        var executor = Get(nodeType);
        if (executor is not null)
        {
            return executor;
        }

        throw new InvalidOperationException($"No workflow node executor is registered for node type '{nodeType}'.");
    }
}
