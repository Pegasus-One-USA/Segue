namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowNodeInput
{
    public WorkflowNodeInput(IReadOnlyCollection<WorkflowNodeOutput> upstreamOutputs)
    {
        UpstreamOutputs = upstreamOutputs;
    }

    public IReadOnlyCollection<WorkflowNodeOutput> UpstreamOutputs { get; }
}
