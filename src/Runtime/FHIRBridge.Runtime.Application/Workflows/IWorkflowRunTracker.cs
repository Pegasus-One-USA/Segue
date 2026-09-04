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
    /// <summary>Registers a tracked in-flight run along with the <see cref="CancellationTokenSource"/> its
    /// execution was started with — the same source <see cref="RequestCancellation"/> later signals, and the
    /// only handle to it (the caller runs detached, via Task.Run, so nothing else holds a reference).</summary>
    void MarkRunning(Guid workflowRunId, CancellationTokenSource cancellationSource);

    /// <summary>Also disposes the run's <see cref="CancellationTokenSource"/> — safe to call whether or not
    /// cancellation was ever requested.</summary>
    void MarkComplete(Guid workflowRunId);

    bool IsRunning(Guid workflowRunId);

    /// <summary>Signals the tracked run's <see cref="CancellationTokenSource"/> so the orchestrator's between-node
    /// check (see RankedWorkflowOrchestrator.RunNodesAsync) stops it after the currently in-flight node finishes.
    /// Returns false if <paramref name="workflowRunId"/> isn't currently tracked as running — already completed,
    /// or never started as a cancellable async run in the first place.</summary>
    bool RequestCancellation(Guid workflowRunId);
}
