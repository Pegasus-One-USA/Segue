using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Durable store for executed <see cref="WorkflowRun"/> aggregates (run + per-node runs + lineage).
/// The orchestrator persists a run once it reaches a terminal state, giving a node-by-node execution
/// history that survives a restart and can be replayed in the pipeline-builder timeline.
/// </summary>
public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun workflowRun, CancellationToken cancellationToken);

    Task<WorkflowRun?> GetAsync(Guid workflowRunId, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent <see cref="WorkflowRunStatus.Validated"/> run for this definition and correlation id, or
    /// null when there is none. Lets <c>POST /run</c> continue the row <c>validate-run</c> already created for
    /// this attempt instead of opening a second one, so one user action is one Execution History row.
    /// <para>Returns null for every caller that never called validate-run (the portal's Run button, the
    /// scheduler, webhooks), which therefore keep creating their own run exactly as before.</para>
    /// </summary>
    /// <summary>
    /// Marks every <see cref="WorkflowRunStatus.Validated"/> run older than <paramref name="olderThanUtc"/> as
    /// <see cref="WorkflowRunStatus.Expired"/>, returning how many were swept.
    /// <para>Validated is non-terminal by design — it is the row <c>/run</c> continues — so an attempt that never
    /// proceeds (the common case: the user was sent to the EHR to sign in and closed the tab) would otherwise sit
    /// there indefinitely, both cluttering the list and staying eligible for continuation long after the caller
    /// has moved on. The cutoff must comfortably exceed a real interactive sign-in.</para>
    /// </summary>
    Task<int> ExpireStaleValidatedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Renews the lease of each <see cref="WorkflowRunStatus.Running"/> run in <paramref name="runIds"/> (the runs this
    /// process is executing) and returns those a user has asked to cancel from another instance. A targeted update:
    /// it never rewrites the run row the executing process saves when the run ends.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> RenewLeasesAsync(
        IReadOnlyCollection<Guid> runIds,
        string owner,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fails every <see cref="WorkflowRunStatus.Running"/> run whose lease lapsed before <paramref name="now"/> (or,
    /// with no lease, that started before <paramref name="legacyStartedBefore"/>), with <paramref name="reason"/> as
    /// its error, returning how many. Safe from any instance: a live run's lease is renewed far more often than it
    /// lapses, so only a run whose process stopped (restart, shutdown, crash) or stopped vouching for it is closed.
    /// </summary>
    Task<int> FailExpiredRunsAsync(
        DateTimeOffset now,
        DateTimeOffset legacyStartedBefore,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>Records that a user asked to cancel the <see cref="WorkflowRunStatus.Running"/> run, for the instance
    /// executing it to act on. Returns false when the run is not Running. A targeted update, like
    /// <see cref="RenewLeasesAsync"/>.</summary>
    Task<bool> RequestCancellationAsync(Guid workflowRunId, DateTimeOffset requestedAt, CancellationToken cancellationToken);

    Task<WorkflowRun?> FindValidatedAsync(
        Guid workflowDefinitionId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<WorkflowRun>> ListByDefinitionAsync(
        Guid workflowDefinitionId,
        CancellationToken cancellationToken);

    /// <summary>Most recent runs across every workflow definition, newest first — backs the global Execution History list.</summary>
    Task<IReadOnlyCollection<WorkflowRun>> ListRecentAsync(
        int count,
        CancellationToken cancellationToken);

    /// <summary>All-time run count per status, across every workflow definition that still exists (a deleted workflow's
    /// orphaned runs are excluded, matching the Execution History list) — every <see cref="WorkflowRunStatus"/>
    /// value is present even if its count is 0. Backs the Dashboard's status stat tiles; unlike
    /// <see cref="ListRecentAsync"/>, this is not capped to the most recent N runs.</summary>
    Task<IReadOnlyDictionary<WorkflowRunStatus, int>> GetStatusCountsAsync(CancellationToken cancellationToken);
}
