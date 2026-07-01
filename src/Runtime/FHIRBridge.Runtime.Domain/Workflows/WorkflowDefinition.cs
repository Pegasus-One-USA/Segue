namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowDefinition
{
    private readonly List<WorkflowNode> _nodes = [];
    private readonly List<WorkflowEdge> _edges = [];

    public WorkflowDefinition(Guid id, string name, int version)
        : this(id, Guid.Empty, name, version, isEnabled: true)
    {
    }

    public WorkflowDefinition(Guid id, Guid tenantId, string name, int version, bool isEnabled = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Workflow name is required.", nameof(name));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Workflow version must be greater than zero.");
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        TenantId = tenantId;
        Name = name.Trim();
        Version = version;
        IsEnabled = isEnabled;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public string Name { get; }

    public int Version { get; }

    public bool IsEnabled { get; private set; }

    public bool IsActive => IsEnabled;

    public IReadOnlyCollection<WorkflowNode> Nodes => _nodes;

    public IReadOnlyCollection<WorkflowEdge> Edges => _edges;

    public WorkflowNode AddNode(
        string nodeType,
        WorkflowNodeCategory category,
        int rank,
        int subRank = 0,
        string? displayName = null,
        string configurationJson = "{}",
        double positionX = 0,
        double positionY = 0,
        bool isEnabled = true)
    {
        var node = new WorkflowNode(
            Guid.NewGuid(),
            Id,
            nodeType,
            category,
            rank,
            subRank,
            displayName ?? nodeType,
            configurationJson,
            positionX,
            positionY,
            isEnabled);

        _nodes.Add(node);
        return node;
    }

    public WorkflowEdge AddEdge(Guid fromNodeId, Guid toNodeId)
    {
        var edge = new WorkflowEdge(Guid.NewGuid(), Id, fromNodeId, toNodeId);
        _edges.Add(edge);
        return edge;
    }

    public WorkflowNodeConfiguration AddNodeConfiguration(Guid nodeId, string key, string value)
    {
        var node = _nodes.SingleOrDefault(candidate => candidate.Id == nodeId)
            ?? throw new InvalidOperationException($"Workflow node '{nodeId}' does not exist.");

        return node.AddConfiguration(key, value);
    }

    public void Activate() => IsEnabled = true;

    public void Deactivate() => IsEnabled = false;
}
