using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowRunResult
{
    public WorkflowRunResult(WorkflowRun workflowRun, IReadOnlyDictionary<Guid, WorkflowNodeOutput> outputsByNodeId)
    {
        WorkflowRun = workflowRun;
        OutputsByNodeId = outputsByNodeId;
    }

    public WorkflowRun WorkflowRun { get; }

    public IReadOnlyDictionary<Guid, WorkflowNodeOutput> OutputsByNodeId { get; }
}
