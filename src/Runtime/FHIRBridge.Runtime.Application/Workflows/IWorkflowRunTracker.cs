namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// Tracks workflow runs that are executing in the background (see the "async" branch of
/// <c>POST /workflows/{workflowId}/run</c>), for the window between "run started" and "run persisted".
/// <see cref="IWorkflowRunStore"/> only ever sees a run once it reaches a terminal state (see
/// <c>SqlWorkflowRunStore</c>'s write-once contract), so a caller polling for status while the run is still
/// executing needs somewhere else to look — this is that somewhere else. Singleton, in-memory, and deliberately
/// not persisted: a process restart mid-run means the run's own audit trail/terminal row is the source of truth,
/// not this tracker.
/// </summary>
public interface IWorkflowRunTracker
{
    void MarkRunning(Guid workflowRunId);

    void MarkComplete(Guid workflowRunId);

    bool IsRunning(Guid workflowRunId);
}
