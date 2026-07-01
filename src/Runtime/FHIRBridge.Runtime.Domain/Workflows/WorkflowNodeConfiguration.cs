namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowNodeConfiguration
{
    public WorkflowNodeConfiguration(Guid id, Guid workflowNodeId, string key, string value, bool isSecret = false)
    {
        if (workflowNodeId == Guid.Empty)
        {
            throw new ArgumentException("Workflow node id is required.", nameof(workflowNodeId));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Configuration key is required.", nameof(key));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowNodeId = workflowNodeId;
        Key = key.Trim();
        Value = value;
        IsSecret = isSecret;
    }

    public Guid Id { get; }

    public Guid WorkflowNodeId { get; }

    public string Key { get; }

    public string Value { get; }

    public bool IsSecret { get; }
}
