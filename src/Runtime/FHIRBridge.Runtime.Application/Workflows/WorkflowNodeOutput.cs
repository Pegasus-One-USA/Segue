namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowNodeOutput
{
    public WorkflowNodeOutput(
        Guid nodeId,
        string nodeType,
        object? payload,
        WorkflowDataContract contract = WorkflowDataContract.None,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        NodeId = nodeId;
        NodeType = nodeType;
        Payload = payload;
        Contract = contract;
        Metadata = metadata ?? new Dictionary<string, object?>();
    }

    public Guid NodeId { get; }

    public string NodeType { get; }

    public object? Payload { get; }

    public WorkflowDataContract Contract { get; }

    public IReadOnlyDictionary<string, object?> Metadata { get; }
}
