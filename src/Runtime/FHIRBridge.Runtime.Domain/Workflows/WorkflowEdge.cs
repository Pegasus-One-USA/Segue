namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowEdge
{
    public WorkflowEdge(Guid id, Guid workflowDefinitionId, Guid fromNodeId, Guid toNodeId)
    {
        if (workflowDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Workflow definition id is required.", nameof(workflowDefinitionId));
        }

        if (fromNodeId == Guid.Empty)
        {
            throw new ArgumentException("From node id is required.", nameof(fromNodeId));
        }

        if (toNodeId == Guid.Empty)
        {
            throw new ArgumentException("To node id is required.", nameof(toNodeId));
        }

        if (fromNodeId == toNodeId)
        {
            throw new ArgumentException("Workflow edges cannot point to the same node.");
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowDefinitionId = workflowDefinitionId;
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
    }

    public Guid Id { get; }

    public Guid WorkflowDefinitionId { get; }

    public Guid FromNodeId { get; }

    public Guid ToNodeId { get; }
}
