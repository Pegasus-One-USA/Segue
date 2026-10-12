using System.Linq.Expressions;
using System.Reflection;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

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
        //
        // Status is a concurrency token (see WorkflowRunEntityTypeConfiguration): this UPDATE, and the placeholder
        // DELETE below, apply only while the row still has the status this process last saw. A row another instance
        // closed meanwhile (failed as interrupted, cancelled as abandoned) is never overwritten with this outcome.
        if (_dbContext.ChangeTracker.Entries<WorkflowRun>().Any(entry => entry.Entity.Id == workflowRun.Id))
        {
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw await AlreadyClosedAsync(workflowRun.Id, cancellationToken);
            }

            return;
        }

        var existing = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(run => run.Id == workflowRun.Id, cancellationToken);

        if (existing is not null)
        {
            if (WorkflowRunStatuses.IsTerminal(existing.Status))
            {
                // Already-persisted terminal row (e.g. a retried write) — preserve the original idempotent-insert
                // guarantee for a run that's actually finished. A different outcome than the one recorded means
                // someone else closed the run: said so, rather than dropped silently.
                if (existing.Status != workflowRun.Status)
                {
                    throw new WorkflowRunAlreadyClosedException(workflowRun.Id, existing.Status.ToString());
                }

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
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Closed (or otherwise changed) between the read above and this DELETE.
                _dbContext.Entry(existing).State = EntityState.Detached;
                throw await AlreadyClosedAsync(workflowRun.Id, cancellationToken);
            }
        }

        await _dbContext.WorkflowRuns.AddAsync(workflowRun, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Leaves the run untracked (this context must not retry the refused write) and names the status that
    /// stands.</summary>
    private async Task<WorkflowRunAlreadyClosedException> AlreadyClosedAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries()
                     .Where(e => e.Entity is WorkflowRun run && run.Id == workflowRunId
                         || e.Entity is WorkflowNodeRun nodeRun && nodeRun.WorkflowRunId == workflowRunId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        var recorded = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(run => run.Id == workflowRunId)
            .Select(run => (WorkflowRunStatus?)run.Status)
            .FirstOrDefaultAsync(cancellationToken);
        return new WorkflowRunAlreadyClosedException(workflowRunId, recorded?.ToString() ?? "deleted");
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

    public async Task<WorkflowLeaseRenewal> RenewLeasesAsync(
        IReadOnlyCollection<Guid> runIds,
        string owner,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return WorkflowLeaseRenewal.None;
        }

        var ids = runIds.ToArray();
        // Targeted updates, not SaveAsync: the executing process saves the whole run row when the run ends, and a
        // heartbeat must never rewrite (or be rewritten by) that.
        await _dbContext.WorkflowRuns
            .Where(run => ids.Contains(run.Id) && run.Status == WorkflowRunStatus.Running)
            .ExecuteUpdateAsync(
                set => set.SetProperty(run => run.LeaseOwner, owner).SetProperty(run => run.LeaseExpiresAt, expiresAt),
                cancellationToken);

        // Read after the update: a Running row here carries the lease just written. A row with no record yet (its
        // Running placeholder could not be saved) is in none of the lists.
        var rows = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(run => ids.Contains(run.Id))
            .Select(run => new { run.Id, run.Status, run.CancellationRequestedAt })
            .ToListAsync(cancellationToken);
        var running = rows.Where(r => r.Status == WorkflowRunStatus.Running).ToList();
        return new WorkflowLeaseRenewal(
            running.Select(r => r.Id).ToArray(),
            running.Where(r => r.CancellationRequestedAt != null).Select(r => r.Id).ToArray(),
            rows.Where(r => WorkflowRunStatuses.IsTerminal(r.Status)).Select(r => r.Id).ToArray());
    }

    public async Task<int> FailExpiredRunsAsync(
        DateTimeOffset now,
        DateTimeOffset legacyStartedBefore,
        string reason,
        CancellationToken cancellationToken)
    {
        // WorkflowRun.Fail decides what a failed run records; the write is conditional (see SaveIfAsync), so a run
        // that finished or renewed its lease after it was read here is not overwritten with "Interrupted".
        var candidates = await _dbContext.WorkflowRuns.Where(Abandoned(now, legacyStartedBefore)).ToListAsync(cancellationToken);
        var failed = 0;
        foreach (var run in candidates)
        {
            run.Fail(reason, now);
            failed += await SaveIfAsync(run, Abandoned(now, legacyStartedBefore), cancellationToken) ? 1 : 0;
        }

        return failed;
    }

    public async Task<bool> CancelAbandonedAsync(
        Guid workflowRunId,
        DateTimeOffset now,
        DateTimeOffset legacyStartedBefore,
        string reason,
        CancellationToken cancellationToken)
    {
        var run = await _dbContext.WorkflowRuns
            .Where(Abandoned(now, legacyStartedBefore))
            .FirstOrDefaultAsync(r => r.Id == workflowRunId, cancellationToken);
        if (run is null)
        {
            return false;
        }

        run.Cancel(reason, now);
        return await SaveIfAsync(run, Abandoned(now, legacyStartedBefore), cancellationToken);
    }

    /// <summary>Running, and nothing is vouching for it: its lease lapsed, or, from before leases, it is old. The SQL
    /// form of <see cref="WorkflowRun.IsLeaseExpired"/>.</summary>
    private static Expression<Func<WorkflowRun, bool>> Abandoned(DateTimeOffset now, DateTimeOffset legacyStartedBefore) =>
        run => run.Status == WorkflowRunStatus.Running
            && (run.LeaseExpiresAt != null ? run.LeaseExpiresAt < now : run.StartedAt < legacyStartedBefore);

    /// <summary>
    /// Writes the columns a domain method just changed on the tracked <paramref name="run"/> in one UPDATE whose WHERE
    /// is the run's id and <paramref name="stillHolds"/>, so the condition the change was decided on is checked in the
    /// statement that writes it. The column list comes from EF's change tracking, never a list kept here: whatever
    /// the domain method sets is written. Returns whether the row was updated; the run is left untracked either way.
    /// </summary>
    private async Task<bool> SaveIfAsync(WorkflowRun run, Expression<Func<WorkflowRun, bool>> stillHolds, CancellationToken cancellationToken)
    {
        var entry = _dbContext.Entry(run);
        entry.DetectChanges();
        var modified = entry.Properties.Where(p => p.IsModified).ToList();
        if (modified.Count == 0)
        {
            entry.State = EntityState.Detached;
            return false;
        }

        var setters = Expression.Parameter(typeof(SetPropertyCalls<WorkflowRun>), "set");
        Expression body = setters;
        foreach (var property in modified)
        {
            var type = property.Metadata.ClrType;
            var row = Expression.Parameter(typeof(WorkflowRun), "row");
            var column = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(WorkflowRun), type),
                Expression.Property(row, property.Metadata.PropertyInfo!),
                row);
            body = Expression.Call(body, SetPropertyToValue.MakeGenericMethod(type), column, Expression.Constant(property.CurrentValue, type));
        }

        entry.State = EntityState.Detached;

        var id = run.Id;
        var updated = await _dbContext.WorkflowRuns
            .Where(r => r.Id == id)
            .Where(stillHolds)
            .ExecuteUpdateAsync(
                Expression.Lambda<Func<SetPropertyCalls<WorkflowRun>, SetPropertyCalls<WorkflowRun>>>(body, setters),
                cancellationToken);
        return updated > 0;
    }

    // SetPropertyCalls<T>.SetProperty<TProperty>(Func<T, TProperty> property, TProperty value).
    private static readonly MethodInfo SetPropertyToValue = typeof(SetPropertyCalls<WorkflowRun>)
        .GetMethods()
        .Single(m => m.Name == nameof(SetPropertyCalls<WorkflowRun>.SetProperty)
            && m.GetParameters() is [_, { ParameterType.IsGenericParameter: true }]);

    public async Task<bool> RequestCancellationAsync(Guid workflowRunId, DateTimeOffset requestedAt, CancellationToken cancellationToken) =>
        await _dbContext.WorkflowRuns
            .Where(run => run.Id == workflowRunId && run.Status == WorkflowRunStatus.Running)
            .ExecuteUpdateAsync(
                set => set.SetProperty(run => run.CancellationRequestedAt, run => run.CancellationRequestedAt ?? requestedAt),
                cancellationToken) > 0;

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
        // Runs of a deleted workflow are left behind in WorkflowRuns, but /workflow-runs drops them (it can't resolve
        // their workflow), so counting them here made the Dashboard tiles disagree with the table beneath them.
        var counts = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(run => _dbContext.WorkflowDefinitions.Any(definition => definition.Id == run.WorkflowDefinitionId))
            .GroupBy(run => run.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return Enum.GetValues<WorkflowRunStatus>()
            .ToDictionary(status => status, status => counts.FirstOrDefault(c => c.Key == status)?.Count ?? 0);
    }
}
