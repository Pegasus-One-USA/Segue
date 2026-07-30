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
            if (existing.Status != WorkflowRunStatus.Running)
            {
                // Already-persisted terminal row (e.g. a retried write) — preserve the original idempotent-insert
                // guarantee for a run that's actually finished.
                return;
            }

            // Replace the in-flight "Running" placeholder with the fully-populated terminal aggregate. The FK's
            // ON DELETE CASCADE (see WorkflowRunEntityTypeConfiguration) takes the placeholder's WorkflowNodeRuns
            // rows with it — none exist yet at the point the placeholder itself was written, so nothing to lose.
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
}
