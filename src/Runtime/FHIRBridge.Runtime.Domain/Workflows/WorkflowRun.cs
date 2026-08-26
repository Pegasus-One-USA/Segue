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
        int workflowDefinitionVersion = 1,
        string? correlationId = null)
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
        CorrelationId = correlationId;
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

    /// <summary>The Global Exception Manager's <c>ERR-yyyyMMdd-NNNNNN</c> id for this run's terminal failure (or
    /// the informational capture for a cancellation/partial-success), when one was actually persisted to
    /// ErrorLogs — set via <see cref="SetErrorReference"/> after <c>Fail</c>/<c>Cancel</c>/<c>PartialSucceed</c>.
    /// Null whenever no capture ran (no exception manager registered) or the capture itself failed to persist —
    /// never a placeholder, so the portal only ever offers a reference id that Operations → Errors can resolve.</summary>
    public string? ErrorReferenceId { get; private set; }

    /// <summary>Who/what launched the run (user audit name, scheduler, or interactive-launch source).</summary>
    public string? TriggeredBy { get; }

    /// <summary>How the run was launched: Manual, Scheduled, or InteractiveLaunch.</summary>
    public string? TriggerType { get; }

    /// <summary>Set when this run is a checkpoint run (restricted to one node's ancestor closure) — null for a normal,
    /// full-graph run. Lets the checkpoint-result endpoint resolve which node to read back from just the run id.</summary>
    public Guid? TargetNodeId { get; }

    /// <summary>Shared execution-tracking id for this run — the same value flows into audit records, captured
    /// errors, and correlation search so every artifact of this run can be found from one id.</summary>
    public string? CorrelationId { get; }

    public IReadOnlyCollection<WorkflowNodeRun> NodeRuns => _nodeRuns;

    public void AddNodeRun(WorkflowNodeRun nodeRun) => _nodeRuns.Add(nodeRun);

    /// <summary>Pauses the run at a source node that deferred to an async bulk-export job — deliberately does not
    /// set <see cref="CompletedAt"/>, since this is not a terminal state; <see cref="Succeed"/>/<see cref="Fail"/>
    /// are still called once the poller resumes execution and the run actually finishes.</summary>
    public void AwaitBulkExport()
    {
        Status = WorkflowRunStatus.AwaitingBulkExport;
    }

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

    /// <summary>Every node ran, but one or more non-parent resource types were skipped for lack of authorization —
    /// distinct from <see cref="Fail"/> since the rest of the run's output is still valid and was written.</summary>
    public void PartialSucceed(string summaryMessage, DateTimeOffset completedAt)
    {
        ErrorMessage = summaryMessage;
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.PartialSuccess;
    }

    /// <summary>The run never reached extraction of anything downstream: a parent/cohort-seeding resource type
    /// (e.g. Patient) wasn't authorized, so the whole run was aborted up front rather than left to fail node-by-node.</summary>
    public void Cancel(string reason, DateTimeOffset completedAt)
    {
        ErrorMessage = reason;
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.Cancelled;
    }

    /// <summary>Records the Global Exception Manager's reference id for this run's Fail/Cancel/PartialSucceed
    /// outcome. Called only after the capture call has actually returned — pass null (a no-op past the initial
    /// state) when no exception manager was registered or the capture failed to persist.</summary>
    public void SetErrorReference(string? referenceId)
    {
        if (referenceId is not null)
        {
            ErrorReferenceId = referenceId;
        }
    }
}
