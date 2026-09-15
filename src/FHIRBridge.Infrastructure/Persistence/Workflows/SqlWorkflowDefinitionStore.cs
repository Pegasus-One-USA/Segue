using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services.Workflows.Numbering;
using FHIRBridge.Observability.Logging;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ICurrentUserService _currentUserService;
    private readonly IWorkflowNumberGenerator _workflowNumberGenerator;
    private readonly ILogger<SqlWorkflowDefinitionStore> _logger;

    public SqlWorkflowDefinitionStore(
        FHIRBridgeDbContext dbContext,
        ICurrentUserService currentUserService,
        IWorkflowNumberGenerator workflowNumberGenerator,
        ILogger<SqlWorkflowDefinitionStore>? logger = null)
    {
        _dbContext = dbContext;
        _currentUserService = currentUserService;
        _workflowNumberGenerator = workflowNumberGenerator;
        _logger = logger ?? NullLogger<SqlWorkflowDefinitionStore>.Instance;
    }

    public async Task<WorkflowDefinition> SaveAsync(
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        // Reuse an ambient transaction (e.g. the caller wrapping this save alongside other work — see
        // WorkflowEndpoints "/workflows/build") instead of nesting a second one on the same connection,
        // which SQL Server rejects outright. Only start and commit our own transaction when none exists.
        var ownsTransaction = _dbContext.Database.IsRelational() && _dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var existing = await _dbContext.WorkflowDefinitions
            .Include(definition => definition.Nodes)

            .Include(definition => definition.Edges)
            .AsSplitQuery()
            .FirstOrDefaultAsync(definition => definition.Id == workflowDefinition.Id, cancellationToken);

        if (existing is not null)
        {
            // Cascade delete removes the tracked nodes/edges/configurations with the parent.
            _dbContext.WorkflowDefinitions.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Stamped here, explicitly, rather than via the generic IAuditableEntity/AuditingSaveChangesInterceptor
        // mechanism: this save is always a delete+re-add (see remarks above), so EF always reports "Added" —
        // relying on the interceptor would reset CreatedOnUtc/CreatedBy to "now" on every edit. CreatedOnUtc/
        // CreatedBy carry over from the row just deleted; UpdatedOnUtc/UpdatedBy only get set from the second
        // save onward (mirrors AuditableChildEntity's ModifiedOnUtc staying null until an actual update).
        var utcNow = DateTime.UtcNow;
        var actor = _currentUserService.CurrentUser.AuditName;
        workflowDefinition.StampAudit(
            createdOnUtc: existing?.CreatedOnUtc ?? utcNow,
            createdBy: existing?.CreatedBy ?? actor,
            updatedOnUtc: existing is not null ? utcNow : null,
            updatedBy: existing is not null ? actor : null);

        // Tells LicenseEnforcementSaveChangesInterceptor this Add is a genuine new workflow (existing is null)
        // rather than the second half of a same-id edit's delete-then-re-add — see the flag's own remarks on
        // FHIRBridgeDbContext. Reset immediately after this save so it can never leak into a later, unrelated
        // SaveChangesAsync call on this same scoped context instance.
        // Carried over from the row being replaced on an edit, allocated fresh only on a genuine create.
        // This save is ALWAYS a delete-and-re-add (see remarks above), so without this an edit would mint a
        // brand-new number every time — renumbering a workflow users may already have quoted, and burning a
        // counter value per save. Same reasoning as CreatedOnUtc/CreatedBy just above.
        //
        // Allocation deliberately happens inside this method's transaction: a create that fails after this
        // point must roll the counter back with it rather than leaving a permanent gap.
        workflowDefinition.SetWorkflowNumber(
            existing?.WorkflowNumber
            ?? await _workflowNumberGenerator.NextAsync(cancellationToken));

        _dbContext.NextWorkflowDefinitionAddIsGenuineCreate = existing is null;
        await _dbContext.WorkflowDefinitions.AddAsync(workflowDefinition, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.NextWorkflowDefinitionAddIsGenuineCreate = false;

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // The trigger is included because it is what the scheduler will act on, and a save is the only moment it
        // changes — an unexpected Manual trigger here explains a workflow that later "never runs" (see
        // Worker.IsWorkflowDue, which skips anything that isn't Schedule or Poll).
        _logger.LogInformation(
            LogEvents.WorkflowDefinitionSaved,
            "Workflow definition '{WorkflowName}' ({WorkflowId}) {SaveKind} as v{WorkflowVersion} by {Actor}: " +
            "{NodeCount} node(s), {EdgeCount} edge(s), IsEnabled={IsEnabled}, Trigger={TriggerType} " +
            "Schedule={ScheduleExpression} IntervalMinutes={IntervalMinutes} TimeZoneId={TimeZoneId}",
            workflowDefinition.Name, workflowDefinition.Id, existing is not null ? "updated" : "created",
            workflowDefinition.Version, actor,
            workflowDefinition.Nodes.Count, workflowDefinition.Edges.Count, workflowDefinition.IsEnabled,
            workflowDefinition.Trigger?.Type, workflowDefinition.Trigger?.ScheduleExpression,
            workflowDefinition.Trigger?.IntervalMinutes, workflowDefinition.Trigger?.TimeZoneId);

        return workflowDefinition;
    }

    public async Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Include(definition => definition.Nodes)

            .Include(definition => definition.Edges)
            .AsSplitQuery()
            .OrderBy(definition => definition.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Include(definition => definition.Nodes)

            .Include(definition => definition.Edges)
            .AsSplitQuery()
            .FirstOrDefaultAsync(definition => definition.Id == workflowId, cancellationToken);
    }

    public async Task DeleteAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.WorkflowDefinitions
            .Include(definition => definition.Nodes)

            .Include(definition => definition.Edges)
            .AsSplitQuery()
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
