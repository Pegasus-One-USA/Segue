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
        bool isEnabled,
        bool checkpointUrlEnabled = false)
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
        CheckpointUrlEnabled = checkpointUrlEnabled;
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

    /// <summary>
    /// Opt-in, per-node-instance flag: when set, this node can generate a "checkpoint" launch URL that runs only
    /// this node's ancestor closure and returns its output — regardless of category or rank. See
    /// docs/backend/05-workflow-node-checkpoints-plan.md.
    /// </summary>
    public bool CheckpointUrlEnabled { get; private set; }

    public IReadOnlyCollection<WorkflowNodeConfiguration> Configuration => _configuration;

    public void SetCheckpointUrlEnabled(bool enabled) => CheckpointUrlEnabled = enabled;

    public WorkflowNodeConfiguration AddConfiguration(string key, string value)
    {
        var configuration = new WorkflowNodeConfiguration(Guid.NewGuid(), Id, key, value);
        _configuration.Add(configuration);
        return configuration;
    }
}
