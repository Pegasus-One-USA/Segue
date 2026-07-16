namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowRun
{
    private readonly List<WorkflowNodeRun> _nodeRuns = [];

    public WorkflowRun(
        Guid id,
        Guid workflowDefinitionId,
        DateTimeOffset startedAt,
        string? triggeredBy = null,
        string? triggerType = null,
        Guid? targetNodeId = null,
        int workflowDefinitionVersion = 1)
    {
        if (workflowDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Workflow definition id is required.", nameof(workflowDefinitionId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowDefinitionId = workflowDefinitionId;
        WorkflowDefinitionVersion = workflowDefinitionVersion;
        StartedAt = startedAt;
        Status = WorkflowRunStatus.Running;
        TriggeredBy = triggeredBy;
        TriggerType = triggerType;
        TargetNodeId = targetNodeId;
    }

    public Guid Id { get; }

    public Guid WorkflowDefinitionId { get; }

    /// <summary>The workflow definition's <c>Version</c> at the moment this run started — so a run can always be
    /// traced back to the exact version that produced it, even after the definition is edited again.</summary>
    public int WorkflowDefinitionVersion { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public WorkflowRunStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Who/what launched the run (user audit name, scheduler, or interactive-launch source).</summary>
    public string? TriggeredBy { get; }

    /// <summary>How the run was launched: Manual, Scheduled, or InteractiveLaunch.</summary>
    public string? TriggerType { get; }

    /// <summary>Set when this run is a checkpoint run (restricted to one node's ancestor closure) — null for a normal,
    /// full-graph run. Lets the checkpoint-result endpoint resolve which node to read back from just the run id.</summary>
    public Guid? TargetNodeId { get; }

    public IReadOnlyCollection<WorkflowNodeRun> NodeRuns => _nodeRuns;

    public void AddNodeRun(WorkflowNodeRun nodeRun) => _nodeRuns.Add(nodeRun);

    public void Succeed(DateTimeOffset completedAt)
    {
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.Succeeded;
    }

    public void Fail(string errorMessage, DateTimeOffset completedAt)
    {
        ErrorMessage = errorMessage;
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.Failed;
    }
}
