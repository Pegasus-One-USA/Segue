using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// SQL-backed <see cref="IWorkflowDefinitionStore"/> (Scenario A). Persists a designed graph — definition,
/// nodes, per-node configuration, and edges — to the control-plane database so graphs survive a restart.
/// </summary>
/// <remarks>
/// A save is a wholesale replace: the API rebuilds the graph with fresh node/edge ids on every update
/// (POST/PUT), while activate/deactivate reuse the same ids. Deleting the old aggregate and re-inserting
/// the new one across two <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> calls handles both
/// without primary-key collisions; a transaction (on relational providers) keeps the swap atomic.
/// </remarks>
public sealed class SqlWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly FHIRBridgeDbContext _dbContext;

    public SqlWorkflowDefinitionStore(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<WorkflowDefinition> SaveAsync(
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var existing = await _dbContext.WorkflowDefinitions
            .Include(definition => definition.Nodes)
                .ThenInclude(node => node.Configuration)
            .Include(definition => definition.Edges)
            .FirstOrDefaultAsync(definition => definition.Id == workflowDefinition.Id, cancellationToken);

        if (existing is not null)
        {
            // Cascade delete removes the tracked nodes/edges/configurations with the parent.
            _dbContext.WorkflowDefinitions.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await _dbContext.WorkflowDefinitions.AddAsync(workflowDefinition, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return workflowDefinition;
    }

    public async Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Include(definition => definition.Nodes)
                .ThenInclude(node => node.Configuration)
            .Include(definition => definition.Edges)
            .OrderBy(definition => definition.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Include(definition => definition.Nodes)
                .ThenInclude(node => node.Configuration)
            .Include(definition => definition.Edges)
            .FirstOrDefaultAsync(definition => definition.Id == workflowId, cancellationToken);
    }

    public async Task DeleteAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.WorkflowDefinitions
            .Include(definition => definition.Nodes)
                .ThenInclude(node => node.Configuration)
            .Include(definition => definition.Edges)
            .FirstOrDefaultAsync(definition => definition.Id == workflowId, cancellationToken);

        if (existing is null)
        {
            return;
        }

        // Cascade delete removes the tracked nodes/edges/configurations with the parent.
        _dbContext.WorkflowDefinitions.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
