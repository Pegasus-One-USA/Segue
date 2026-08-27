using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Domain.Entities;
using FHIRBridge.Runtime.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence.Pipeline;

/// <summary>
/// SQL-backed <see cref="IPipelineRunStore"/> for the Runtime DAG plane (bulk-export / <see
/// cref="FHIRBridge.Runtime.Application.Services.PipelineOrchestrator"/>). Previously this store was in-memory
/// only (<c>InMemoryPipelineRunStore</c>) — every run and its event history vanished on a Worker/API restart, with
/// nothing queryable in SQL. Mirrors <c>SqlWorkflowRunStore</c>'s tracked-vs-detached handling: the orchestrator
/// calls <see cref="AddAsync"/> once then <see cref="UpdateAsync"/> repeatedly against the same tracked
/// <see cref="PipelineRun"/> instance for one run's lifetime, so most updates are just a flush; a detached update
/// from a different DbContext scope replaces the row wholesale unless it's already terminal.
/// </summary>
public sealed class SqlPipelineRunStore : IPipelineRunStore
{
    private readonly FHIRBridgeDbContext _dbContext;

    public SqlPipelineRunStore(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(PipelineRun pipelineRun, CancellationToken cancellationToken)
    {
        if (_dbContext.ChangeTracker.Entries<PipelineRun>().Any(entry => entry.Entity.Id == pipelineRun.Id))
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        await _dbContext.PipelineRuns.AddAsync(pipelineRun, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(PipelineRun pipelineRun, CancellationToken cancellationToken)
    {
        // The common case: the orchestrator calls Add then Update repeatedly against this same DbContext instance
        // for one run — already tracked (Unchanged from AddAsync, or Modified from a prior UpdateAsync), so EF's
        // own change detection has already picked up Fail()/Complete()/StartStep()'s in-place mutations. Just flush.
        if (_dbContext.ChangeTracker.Entries<PipelineRun>().Any(entry => entry.Entity.Id == pipelineRun.Id))
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var existing = await _dbContext.PipelineRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(run => run.Id == pipelineRun.Id, cancellationToken);

        if (existing is not null)
        {
            if (IsTerminal(existing.Status))
            {
                // Already-persisted terminal row (e.g. a retried write from a stale scope) — don't clobber it with
                // a detached, possibly-stale copy.
                return;
            }

            // Replace the in-flight row wholesale — the FK's ON DELETE CASCADE (see
            // PipelineRunEntityTypeConfiguration) takes its PipelineRunSteps with it; the incoming pipelineRun
            // carries the full, up-to-date Steps collection to reinsert.
            _dbContext.PipelineRuns.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await _dbContext.PipelineRuns.AddAsync(pipelineRun, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PipelineRun?> GetAsync(Guid pipelineRunId, CancellationToken cancellationToken)
    {
        return await _dbContext.PipelineRuns
            .AsNoTracking()
            .Include(run => run.Steps)
            .FirstOrDefaultAsync(run => run.Id == pipelineRunId, cancellationToken);
    }

    public async Task<IReadOnlyList<PipelineRun>> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        return await _dbContext.PipelineRuns
            .AsNoTracking()
            .Include(run => run.Steps)
            .OrderByDescending(run => run.StartedOnUtc)
            .Take(Math.Clamp(count, 1, 1000))
            .ToListAsync(cancellationToken);
    }

    public async Task AddEventAsync(PipelineRunEvent pipelineRunEvent, CancellationToken cancellationToken)
    {
        await _dbContext.PipelineRunEvents.AddAsync(pipelineRunEvent, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PipelineRunEvent>> GetEventsAsync(Guid pipelineRunId, CancellationToken cancellationToken)
    {
        return await _dbContext.PipelineRunEvents
            .AsNoTracking()
            .Where(x => x.PipelineRunId == pipelineRunId)
            .OrderBy(x => x.OccurredOnUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Pending/Running are still in-flight — a placeholder row in either is safe to replace wholesale.
    /// Only Completed/Failed are actually finished.</summary>
    private static bool IsTerminal(PipelineRunStatus status) => status is PipelineRunStatus.Completed or PipelineRunStatus.Failed;
}
