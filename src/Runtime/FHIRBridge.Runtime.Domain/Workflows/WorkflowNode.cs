namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowNode
{
    public WorkflowNode(
        Guid id,
        Guid workflowDefinitionId,
        string nodeType,
        WorkflowNodeCategory category,
        int rank,
        int subRank,
        string displayName,
        string configurationJson,
        double positionX,
        double positionY,
        bool isEnabled)
    {
        if (workflowDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Workflow definition id is required.", nameof(workflowDefinitionId));
        }

        if (string.IsNullOrWhiteSpace(nodeType))
        {
            throw new ArgumentException("Node type is required.", nameof(nodeType));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowDefinitionId = workflowDefinitionId;
        NodeType = nodeType.Trim();
        Category = category;
        Rank = rank;
        SubRank = subRank;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? NodeType : displayName.Trim();
        ConfigurationJson = string.IsNullOrWhiteSpace(configurationJson) ? "{}" : configurationJson;
        PositionX = positionX;
        PositionY = positionY;
        IsEnabled = isEnabled;
    }

    private readonly List<WorkflowNodeConfiguration> _configuration = [];

    public Guid Id { get; }

    public Guid WorkflowDefinitionId { get; }

    public string NodeType { get; }

    public WorkflowNodeCategory Category { get; }

    public int Rank { get; }

    public int SubRank { get; }

    public string DisplayName { get; }

    public string ConfigurationJson { get; }

    public double PositionX { get; }

    public double PositionY { get; }

    public bool IsEnabled { get; }

    public IReadOnlyCollection<WorkflowNodeConfiguration> Configuration => _configuration;

    public WorkflowNodeConfiguration AddConfiguration(string key, string value)
    {
        var configuration = new WorkflowNodeConfiguration(Guid.NewGuid(), Id, key, value);
        _configuration.Add(configuration);
        return configuration;
    }
}
