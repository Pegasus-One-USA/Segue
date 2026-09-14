using FHIRBridge.Application.Abstractions.Security;
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
/// A save is an id-based upsert: nodes/edges present in <paramref name="workflowDefinition"/> that already
/// exist (by id) are updated in place, new ones are inserted, and ones no longer present are removed. This
/// keeps node/edge ids stable across saves — required for anything (an incremental "add node" endpoint, a
/// portal reference) to address a node consistently from one save to the next. <see cref="WorkflowDefinition.Version"/>
/// is a concurrency token (see WorkflowPersistenceConfigurations), so a stale caller's save throws
/// <see cref="DbUpdateConcurrencyException"/> rather than silently overwriting a newer save.
/// </remarks>
public sealed class SqlWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<SqlWorkflowDefinitionStore> _logger;

    public SqlWorkflowDefinitionStore(
        FHIRBridgeDbContext dbContext,
        ICurrentUserService currentUserService,
        ILogger<SqlWorkflowDefinitionStore>? logger = null)
    {
        _dbContext = dbContext;
        _currentUserService = currentUserService;
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
                .ThenInclude(node => node.Configuration)
            .Include(definition => definition.Edges)
            .AsSplitQuery()
            .FirstOrDefaultAsync(definition => definition.Id == workflowDefinition.Id, cancellationToken);

        var utcNow = DateTime.UtcNow;
        var actor = _currentUserService.CurrentUser.AuditName;
        workflowDefinition.StampAudit(
            createdOnUtc: existing?.CreatedOnUtc ?? utcNow,
            createdBy: existing?.CreatedBy ?? actor,
            updatedOnUtc: existing is not null ? utcNow : null,
            updatedBy: existing is not null ? actor : null);

        if (existing is null)
        {
            await _dbContext.WorkflowDefinitions.AddAsync(workflowDefinition, cancellationToken);
        }
        else
        {
            // The real conflict window is the caller's whole edit — load, let the user work, save — not the
            // instant between the fresh read above and SaveChangesAsync below. So the concurrency check must
            // compare the DB's actual current Version against the version the CALLER originally read (baked
            // into workflowDefinition.Version as "previous version + 1" — see WorkflowEndpoints.BuildWorkflow),
            // not against whatever this fresh `existing` query happens to see: by definition that query always
            // agrees with the database, so leaving EF's original-value at `existing`'s tracked value would
            // make this check pass unconditionally, silently letting a second, stale save win.
            if (existing.Version != workflowDefinition.Version - 1)
            {
                throw new DbUpdateConcurrencyException(
                    $"Workflow '{workflowDefinition.Id}' was modified by another save (expected version " +
                    $"{workflowDefinition.Version - 1}, found {existing.Version}).");
            }

            _dbContext.Entry(existing).CurrentValues.SetValues(workflowDefinition);

            // Node/edge ids are the only thing that must survive a save (see class remarks): every property on
            // WorkflowNode/WorkflowEdge/WorkflowNodeConfiguration is otherwise immutable, so an id match is
            // replaced wholesale via the DbSets directly (rather than through the aggregate's own collections,
            // whose change-tracking fixup does not cope with an item being removed and re-added under the same
            // id within one SaveChanges) rather than patched property-by-property.
            UpsertNodes(_dbContext, existing.Id, workflowDefinition.Nodes);
            UpsertEdges(_dbContext, existing.Id, workflowDefinition.Edges);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

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

    /// <summary>Replaces the persisted node graph for <paramref name="workflowDefinitionId"/> with
    /// <paramref name="desired"/> by id: an id present in both is removed and re-added (nodes are immutable
    /// value-ish objects, so a content change always means a new instance), an id only in
    /// <paramref name="desired"/> is inserted, and an id no longer present is removed (cascade-deletes its
    /// configuration). Goes through the DbContext's sets directly rather than the aggregate's own Nodes
    /// collection — removing and re-adding an item under the same id within one SaveChanges does not survive
    /// EF's relationship fixup when done via the tracked navigation collection.</summary>
    private static void UpsertNodes(FHIRBridgeDbContext dbContext, Guid workflowDefinitionId, IReadOnlyCollection<WorkflowNode> desired)
    {
        var existingNodes = dbContext.ChangeTracker.Entries<WorkflowNode>()
            .Select(entry => entry.Entity)
            .Where(node => node.WorkflowDefinitionId == workflowDefinitionId)
            .ToArray();

        dbContext.WorkflowNodes.RemoveRange(existingNodes);

        foreach (var node in desired)
        {
            var copy = new WorkflowNode(
                node.Id,
                workflowDefinitionId,
                node.NodeType,
                node.Category,
                node.Rank,
                node.SubRank,
                node.DisplayName,
                node.ConfigurationJson,
                node.PositionX,
                node.PositionY,
                node.IsEnabled,
                node.CheckpointUrlEnabled);

            foreach (var configuration in node.Configuration)
            {
                copy.AddConfiguration(configuration.Key, configuration.Value);
            }

            dbContext.WorkflowNodes.Add(copy);
        }
    }

    /// <summary>Replaces the persisted edges for <paramref name="workflowDefinitionId"/> with
    /// <paramref name="desired"/>, the same remove-then-reinsert approach as <see cref="UpsertNodes"/>. Must
    /// run after node rows have settled, since an edge references node ids directly.</summary>
    private static void UpsertEdges(FHIRBridgeDbContext dbContext, Guid workflowDefinitionId, IReadOnlyCollection<WorkflowEdge> desired)
    {
        var existingEdges = dbContext.ChangeTracker.Entries<WorkflowEdge>()
            .Select(entry => entry.Entity)
            .Where(edge => edge.WorkflowDefinitionId == workflowDefinitionId)
            .ToArray();

        dbContext.WorkflowEdges.RemoveRange(existingEdges);

        foreach (var edge in desired)
        {
            dbContext.WorkflowEdges.Add(new WorkflowEdge(edge.Id, workflowDefinitionId, edge.FromNodeId, edge.ToNodeId));
        }
    }

    public async Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Include(definition => definition.Nodes)
                .ThenInclude(node => node.Configuration)
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
                .ThenInclude(node => node.Configuration)
            .Include(definition => definition.Edges)
            .AsSplitQuery()
            .FirstOrDefaultAsync(definition => definition.Id == workflowId, cancellationToken);
    }

    public async Task DeleteAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.WorkflowDefinitions
            .Include(definition => definition.Nodes)
                .ThenInclude(node => node.Configuration)
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
