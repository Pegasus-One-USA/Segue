using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

namespace FHIRBridge.Runtime.Infrastructure.Workflows;

/// <summary>
/// Mirrors <see cref="WorkflowNodeExecutorRegistry"/> but wraps every resolved executor in a
/// <see cref="LineageTrackingWorkflowNodeExecutor"/>, so every workflow-graph run reports lineage (and node-level
/// Operational Log narration) without each of the ~45 node executors needing to know about <see cref="ILineageTracker"/>
/// or <see cref="IOperationalAuditService"/> individually.
/// </summary>
public sealed class LineageTrackingWorkflowNodeExecutorRegistry : IWorkflowNodeExecutorRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkflowNodeExecutor> _executorsByNodeType;

    public LineageTrackingWorkflowNodeExecutorRegistry(
        IEnumerable<IWorkflowNodeExecutor> executors,
        ILineageTracker lineageTracker,
        IOperationalAuditService? auditService = null)
    {
        _executorsByNodeType = executors.ToDictionary(
            executor => executor.NodeType,
            executor => (IWorkflowNodeExecutor)new LineageTrackingWorkflowNodeExecutor(executor, lineageTracker, auditService),
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
