namespace FHIRBridge.Runtime.Domain.Workflows;

public sealed class WorkflowNodeRun
{
    public WorkflowNodeRun(
        Guid id,
        Guid workflowRunId,
        Guid workflowNodeId,
        string nodeType,
        int rank,
        int subRank,
        DateTimeOffset startedAt)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowRunId = workflowRunId;
        WorkflowNodeId = workflowNodeId;
        NodeType = nodeType;
        Rank = rank;
        SubRank = subRank;
        StartedAt = startedAt;
        Status = WorkflowRunStatus.Running;
    }

    public Guid Id { get; }

    public Guid WorkflowRunId { get; }

    public Guid WorkflowNodeId { get; }

    public string NodeType { get; }

    public int Rank { get; }

    public int SubRank { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public WorkflowRunStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

        public string? LineageJson { get; private set; }

    public void Succeed(string lineageJson, DateTimeOffset completedAt)
    {
        LineageJson = lineageJson;
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.Succeeded;
    }

    public void Fail(string errorMessage, DateTimeOffset completedAt)
    {
        ErrorMessage = errorMessage;
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.Failed;
    }

    /// <summary>This node's parent/cohort-seeding resource type wasn't authorized, cancelling the whole run — see
    /// <see cref="WorkflowRun.Cancel"/>.</summary>
    public void Cancel(string reason, DateTimeOffset completedAt)
    {
        ErrorMessage = reason;
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.Cancelled;
    }
}
