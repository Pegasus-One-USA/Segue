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

    /// <summary>The source vendor's OWN id for the Bulk Data <c>$export</c> job this run deferred to — the last
    /// path segment of the job's status URL (Epic: <c>.../api/FHIR/BulkRequest/0000000000176E6DC7DB51C0082DA988</c>).
    /// Set by <see cref="AwaitBulkExport"/>, null for every run that never deferred to an async export. Kept on the
    /// run itself (rather than read back through <c>BulkExportJob</c>) so Execution History can show it as a plain
    /// column, and so it survives the job row being cleaned up. Not the job's identity as far as this system is
    /// concerned — <c>BulkExportJob.StatusUrl</c> remains that; this is for operator lookups and for correlating a
    /// run against the vendor's own logs.</summary>
    public string? BulkRequestId { get; private set; }

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

    /// <summary>The caller's parameters passed validate-run; the run is now waiting to be executed. Non-terminal,
    /// so deliberately leaves <see cref="CompletedAt"/> unset — <see cref="BeginExecution"/> takes it from here
    /// when the matching /run call arrives, and an attempt that never proceeds simply stays in this state
    /// (visibly abandoned, rather than invisible as it was before validate-run existed).</summary>
    public void MarkValidated()
    {
        Status = WorkflowRunStatus.Validated;
    }

    /// <summary>Ages out a <see cref="WorkflowRunStatus.Validated"/> attempt that was never executed. Terminal,
    /// so it stops being a continuation candidate. Deliberately records a reason: an expired row is otherwise
    /// indistinguishable from one that failed silently, and the difference matters when reading the list.</summary>
    public void Expire(DateTimeOffset expiredAt)
    {
        ErrorMessage = "Validated but never executed — the attempt was abandoned before the run started.";
        CompletedAt = expiredAt;
        Status = WorkflowRunStatus.Expired;
    }

    /// <summary>The caller's parameters were refused. Terminal, and reached without executing a single node or
    /// making a single outbound call — which is exactly why it is not <see cref="Fail"/>.</summary>
    public void FailValidation(string errorMessage, DateTimeOffset completedAt)
    {
        ErrorMessage = errorMessage;
        CompletedAt = completedAt;
        Status = WorkflowRunStatus.ValidationFailed;
    }


    /// <summary>Pauses the run at a source node that deferred to an async bulk-export job — deliberately does not
    /// set <see cref="CompletedAt"/>, since this is not a terminal state; <see cref="Succeed"/>/<see cref="Fail"/>
    /// are still called once the poller resumes execution and the run actually finishes.
    /// <paramref name="bulkRequestId"/> is the vendor's own export-job id (see <see cref="BulkRequestId"/>); it is
    /// optional because a status URL a vendor shapes differently may not yield one, and a run that paused is still
    /// correctly paused either way — only the operator-facing lookup is unavailable.</summary>
    public void AwaitBulkExport(string? bulkRequestId = null)
    {
        Status = WorkflowRunStatus.AwaitingBulkExport;

        // Only ever set, never cleared: a resumed run that defers a second time keeps the first id if the second
        // kick-off couldn't produce one, which is strictly more useful than blanking it.
        if (!string.IsNullOrWhiteSpace(bulkRequestId))
        {
            BulkRequestId = bulkRequestId;
        }
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
