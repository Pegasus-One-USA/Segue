namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowDefinition
{
    private readonly List<WorkflowNode> _nodes = [];
    private readonly List<WorkflowEdge> _edges = [];

    public WorkflowDefinition(
        Guid id,
        string name,
        int version,
        bool isEnabled = true,
        bool isPubliclyLaunchable = false,
        string? description = null)
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
        Name = name.Trim();
        Version = version;
        IsEnabled = isEnabled;
        IsPubliclyLaunchable = isPubliclyLaunchable;
        Description = NormalizeDescription(description);
    }

    public Guid Id { get; }

    public string Name { get; }

    /// <summary>Free-text, multi-line notes the admin gave this workflow alongside its name (what it moves, why it
    /// exists, who owns it). Purely descriptive — nothing in the engine reads it. Null when never filled in;
    /// whitespace-only input is normalized to null rather than stored as a blank string.</summary>
    public string? Description { get; private set; }

    public int Version { get; private set; }

    public bool IsEnabled { get; private set; }

    public bool IsActive => IsEnabled;

    /// <summary>Admin-opted-in gate for the anonymous public-standalone-url mint endpoint (see OAuthController.
    /// GetPublicWorkflowStandaloneUrl) — without it, any caller who knew this workflow's id could otherwise mint a
    /// working Epic-login link for it, since minting itself needs no PHI and no session. Default false: a workflow
    /// only becomes externally launchable by an explicit admin action, never implicitly.</summary>
    public bool IsPubliclyLaunchable { get; private set; }

    /// <summary>Workflow-level scheduling metadata (null ⇒ manual / launched). Read by the Worker to fire schedules.</summary>
    public WorkflowTrigger? Trigger { get; private set; }

    /// <summary>Last time the scheduler claimed this workflow for a run; null means never fired by the scheduler.</summary>
    public DateTime? LastTriggeredOnUtc { get; private set; }

    /// <summary>Creation/modification provenance. Stamped explicitly by SqlWorkflowDefinitionStore.SaveAsync rather
    /// than via the generic IAuditableEntity/AuditingSaveChangesInterceptor mechanism used elsewhere, because that
    /// store deletes and re-adds the whole row graph on every save (never a true in-place update) — the interceptor
    /// would otherwise see every save as "Added" and reset CreatedOnUtc each time.</summary>
    public DateTime CreatedOnUtc { get; private set; }

    public string? CreatedBy { get; private set; }

    /// <summary>Null until this definition has been saved a second time — mirrors AuditableChildEntity's own
    /// ModifiedOnUtc staying null until an actual update happens.</summary>
    public DateTime? UpdatedOnUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public IReadOnlyCollection<WorkflowNode> Nodes => _nodes;

    public IReadOnlyCollection<WorkflowEdge> Edges => _edges;

    /// <summary>Sets (or clears) the scheduling trigger. Manual/null means the workflow only runs on demand.</summary>
    public void SetTrigger(WorkflowTrigger? trigger) => Trigger = trigger;

    /// <summary>Records that the scheduler claimed this workflow for a run at <paramref name="triggeredOnUtc"/>.</summary>
    public void MarkTriggered(DateTime triggeredOnUtc) => LastTriggeredOnUtc = triggeredOnUtc;

    public WorkflowNode AddNode(
        string nodeType,
        WorkflowNodeCategory category,
        int rank,
        int subRank = 0,
        string? displayName = null,
        string configurationJson = "{}",
        double positionX = 0,
        double positionY = 0,
        bool isEnabled = true,
        bool checkpointUrlEnabled = false,
        Guid nodeId = default)
    {
        var node = new WorkflowNode(
            nodeId,
            Id,
            nodeType,
            category,
            rank,
            subRank,
            displayName ?? nodeType,
            configurationJson,
            positionX,
            positionY,
            isEnabled,
            checkpointUrlEnabled);

        _nodes.Add(node);
        return node;
    }

    /// <summary>Projects this definition onto a restricted node/edge subset (e.g. a checkpoint's ancestor closure).
    /// Used only for in-flight execution — never persisted — so it deliberately does not go through AddNode/AddEdge.</summary>
    public WorkflowDefinition WithNodesAndEdges(IReadOnlyCollection<WorkflowNode> nodes, IReadOnlyCollection<WorkflowEdge> edges)
    {
        var projected = new WorkflowDefinition(Id, Name, Version, IsEnabled, IsPubliclyLaunchable, Description);
        projected._nodes.AddRange(nodes);
        projected._edges.AddRange(edges);
        return projected;
    }

    public WorkflowEdge AddEdge(Guid fromNodeId, Guid toNodeId, Guid edgeId = default)
    {
        var edge = new WorkflowEdge(edgeId, Id, fromNodeId, toNodeId);
        _edges.Add(edge);
        return edge;
    }

    /// <summary>Removes a node and any edges touching it. No-op if the node does not exist.</summary>
    public void RemoveNode(Guid nodeId)
    {
        _nodes.RemoveAll(node => node.Id == nodeId);
        _edges.RemoveAll(edge => edge.FromNodeId == nodeId || edge.ToNodeId == nodeId);
    }

    /// <summary>Removes a single edge. No-op if the edge does not exist.</summary>
    public void RemoveEdge(Guid edgeId)
    {
        _edges.RemoveAll(edge => edge.Id == edgeId);
    }

    public WorkflowNodeConfiguration AddNodeConfiguration(Guid nodeId, string key, string value)
    {
        var node = _nodes.SingleOrDefault(candidate => candidate.Id == nodeId)
            ?? throw new InvalidOperationException($"Workflow node '{nodeId}' does not exist.");

        return node.AddConfiguration(key, value);
    }

    /// <summary>Replaces the free-text description (null / whitespace clears it).</summary>
    public void SetDescription(string? description) => Description = NormalizeDescription(description);

    public void Activate() => IsEnabled = true;

    public void Deactivate() => IsEnabled = false;

    public void EnablePublicLaunch() => IsPubliclyLaunchable = true;

    public void DisablePublicLaunch() => IsPubliclyLaunchable = false;

    /// <summary>Advances the optimistic-concurrency token by one. Every save path must call this exactly once
    /// (whether it rebuilds the whole definition, as BuildWorkflow's callers do, or mutates one already-loaded
    /// instance in place, as the activate/deactivate/public-launch toggles do) — SqlWorkflowDefinitionStore
    /// rejects a save whose Version isn't the stored row's current Version plus one.</summary>
    public void BumpVersion() => Version++;

    /// <summary>Sets creation/modification provenance — called by SqlWorkflowDefinitionStore.SaveAsync, which
    /// resolves createdOnUtc/createdBy from the row being replaced (or "now"/the current user on a true first
    /// save) since this store's delete-and-recreate save pattern can't rely on EF's Added/Modified state.</summary>
    public void StampAudit(DateTime createdOnUtc, string? createdBy, DateTime? updatedOnUtc, string? updatedBy)
    {
        CreatedOnUtc = createdOnUtc;
        CreatedBy = createdBy;
        UpdatedOnUtc = updatedOnUtc;
        UpdatedBy = updatedBy;
    }

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
