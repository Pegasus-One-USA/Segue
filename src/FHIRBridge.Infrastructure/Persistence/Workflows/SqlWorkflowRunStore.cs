using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// SQL-backed <see cref="IWorkflowRunStore"/> (Scenario A). Persists the run aggregate — the run plus its
/// per-node timeline and lineage — so a launch/run leaves a durable, node-by-node execution history.
/// </summary>
/// <remarks>
/// <see cref="RankedWorkflowOrchestrator"/> now saves this twice per run: once immediately when the run starts
/// (status Running — see its PersistRunStartedAsync), and once more when it reaches a terminal state (Succeeded/
/// Failed), so the Dashboard/Workflow List can show "Running" as a real, queryable row for the run's whole
/// in-flight duration instead of only ever seeing it retroactively once finished. A terminal write REPLACES the
/// still-Running placeholder row wholesale (delete + re-insert, cascading to WorkflowNodeRuns via its FK) rather
/// than attempting an EF change-tracked merge across what are typically two separate DbContext scopes. Once a
/// run is genuinely terminal, though, the original "write once, immutable" guarantee still holds — a retried
/// write against an already-terminal row remains a no-op, so a retry can never duplicate or corrupt history.
/// </remarks>
public sealed class SqlWorkflowRunStore : IWorkflowRunStore
{
    private readonly FHIRBridgeDbContext _dbContext;

    public SqlWorkflowRunStore(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SaveAsync(WorkflowRun workflowRun, CancellationToken cancellationToken)
    {
        // RankedWorkflowOrchestrator calls SaveAsync twice against this same DbContext instance for one run:
        // once up front (Running placeholder) and once more at the terminal state. The second call passes the
        // exact same WorkflowRun reference, already tracked (Unchanged) from the first call's AddAsync — so it's
        // already picked up Fail()/Succeed()'s in-place mutations (and any WorkflowNodeRuns appended to its
        // collection during execution) via EF's own change detection. Just flush it. Querying a *second*,
        // AsNoTracking "existing" instance for the same key and Remove()-ing it here — the original approach —
        // throws InvalidOperationException ("already being tracked") because the key is already attached, which
        // aborted this method before the terminal row (or the SignalR notify that follows it) was ever written,
        // leaving the run stuck on "Running" forever.
        if (_dbContext.ChangeTracker.Entries<WorkflowRun>().Any(entry => entry.Entity.Id == workflowRun.Id))
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var existing = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(run => run.Id == workflowRun.Id, cancellationToken);

        if (existing is not null)
        {
            if (IsTerminal(existing.Status))
            {
                // Already-persisted terminal row (e.g. a retried write) — preserve the original idempotent-insert
                // guarantee for a run that's actually finished.
                return;
            }

            // Replace the in-flight placeholder — Running, or AwaitingBulkExport for a run paused on a $export job
            // that's now resuming — with the fully-populated terminal aggregate. Checking for "not terminal" rather
            // than "== Running" matters: a resume's completed WorkflowRun (Succeeded/PartialSuccess/Failed) was
            // previously discarded here because the existing row's status was AwaitingBulkExport, not Running — the
            // run stayed stuck showing AwaitingBulkExport forever even though BulkExportPollWorker had already
            // downloaded every file and finished running the rest of the DAG. The FK's ON DELETE CASCADE (see
            // WorkflowRunEntityTypeConfiguration) takes the placeholder's WorkflowNodeRuns rows with it — none exist
            // yet at the point an AwaitingBulkExport/Running placeholder was written, so nothing to lose.
            _dbContext.WorkflowRuns.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await _dbContext.WorkflowRuns.AddAsync(workflowRun, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<WorkflowRun?> GetAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Include(run => run.NodeRuns)
            .FirstOrDefaultAsync(run => run.Id == workflowRunId, cancellationToken);
    }

    public async Task<int> ExpireStaleValidatedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        // Tracked (unlike the reads around it): Expire() is a domain mutation on each aggregate, and going through
        // it keeps the terminal-state rule in one place rather than duplicating it as a bulk ExecuteUpdate that
        // sets the column directly. The batch is small by nature — these are only ever attempts nobody completed.
        var stale = await _dbContext.WorkflowRuns
            .Where(run => run.Status == WorkflowRunStatus.Validated && run.StartedAt < olderThanUtc)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        var expiredAt = DateTimeOffset.UtcNow;
        foreach (var run in stale)
        {
            run.Expire(expiredAt);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    public async Task<WorkflowRun?> FindValidatedAsync(
        Guid workflowDefinitionId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        // AsNoTracking is load-bearing, not an optimization: the caller only needs this run's id, and the
        // orchestrator then builds its OWN WorkflowRun instance for that same id. If this one were tracked, the
        // orchestrator's SaveAsync would take the "already tracked — just flush" branch above and never persist
        // the real run, leaving the row stuck exactly as validate-run left it.
        return await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Include(run => run.NodeRuns)
            .Where(run => run.WorkflowDefinitionId == workflowDefinitionId
                && run.CorrelationId == correlationId
                && run.Status == WorkflowRunStatus.Validated)
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<WorkflowRun>> ListByDefinitionAsync(
        Guid workflowDefinitionId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Include(run => run.NodeRuns)
            .Where(run => run.WorkflowDefinitionId == workflowDefinitionId)
            .OrderByDescending(run => run.StartedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<WorkflowRun>> ListRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Include(run => run.NodeRuns)
            .OrderByDescending(run => run.StartedAt)
            .Take(Math.Clamp(count, 1, 1000))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<WorkflowRunStatus, int>> GetStatusCountsAsync(CancellationToken cancellationToken)
    {
        var counts = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .GroupBy(run => run.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return Enum.GetValues<WorkflowRunStatus>()
            .ToDictionary(status => status, status => counts.FirstOrDefault(c => c.Key == status)?.Count ?? 0);
    }

    /// <summary>Pending/Running/AwaitingBulkExport are all still in-flight — a placeholder row in any of these is
    /// safe to replace wholesale. Only Succeeded/Failed/Cancelled/PartialSuccess are actually finished.</summary>
    private static bool IsTerminal(WorkflowRunStatus status) => status is
        WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled
        or WorkflowRunStatus.PartialSuccess or WorkflowRunStatus.ValidationFailed or WorkflowRunStatus.Expired;
    // Validated is deliberately NOT terminal: it is the placeholder /run replaces when it continues the attempt.
}
