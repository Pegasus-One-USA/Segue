using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// SQL-backed <see cref="IWorkflowRunStore"/> (Scenario A). Persists the terminal run aggregate — the run
/// plus its per-node timeline and lineage — so a launch/run leaves a durable, node-by-node execution history.
/// </summary>
/// <remarks>
/// A run is written once, when it reaches a terminal state, and is thereafter immutable; the save is an
/// idempotent insert so a retried write cannot duplicate history.
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
        var alreadyPersisted = await _dbContext.WorkflowRuns
            .AnyAsync(run => run.Id == workflowRun.Id, cancellationToken);

        if (alreadyPersisted)
        {
            return;
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
}
