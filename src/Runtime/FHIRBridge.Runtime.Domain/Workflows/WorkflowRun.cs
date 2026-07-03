namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowRun
{
    private readonly List<WorkflowNodeRun> _nodeRuns = [];

    public WorkflowRun(Guid id, Guid workflowDefinitionId, DateTimeOffset startedAt)
    {
        if (workflowDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Workflow definition id is required.", nameof(workflowDefinitionId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowDefinitionId = workflowDefinitionId;
        StartedAt = startedAt;
        Status = WorkflowRunStatus.Running;
    }

    public Guid Id { get; }

    public Guid WorkflowDefinitionId { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public WorkflowRunStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

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
