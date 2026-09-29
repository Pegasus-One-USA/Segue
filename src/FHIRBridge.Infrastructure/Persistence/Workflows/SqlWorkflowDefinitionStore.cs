using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services.Workflows.Numbering;
using FHIRBridge.Governance;
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
    private readonly IGovernanceLogger? _governanceLogger;

    public SqlWorkflowDefinitionStore(
        FHIRBridgeDbContext dbContext,
        ICurrentUserService currentUserService,
        IWorkflowNumberGenerator workflowNumberGenerator,
        ILogger<SqlWorkflowDefinitionStore>? logger = null,
        IGovernanceLogger? governanceLogger = null)
    {
        _dbContext = dbContext;
        _currentUserService = currentUserService;
        _workflowNumberGenerator = workflowNumberGenerator;
        _logger = logger ?? NullLogger<SqlWorkflowDefinitionStore>.Instance;
        _governanceLogger = governanceLogger;
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

        // Snapshotted BEFORE the delete below, which is what makes an "Updated" audit row able to say what the
        // workflow looked like beforehand — nothing survives the delete-and-re-add to reconstruct it from.
        var previousSnapshot = existing is null ? null : SummarizeWorkflow(existing);
        var previousNodes = existing is null ? null : SnapshotNodes(existing);
        var previousEdges = existing is null ? null : SnapshotEdges(existing);

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

        // Audited HERE rather than through AuditingSaveChangesInterceptor: this save is always a delete-and-re-add
        // (see the remarks above), so EF reports "Added" even for an edit. Letting the generic mechanism handle it
        // would log every edit as "Created" plus a spurious "Deleted" — the same reason CreatedOnUtc/CreatedBy are
        // stamped by hand above. This method already knows which it is, via `existing`.
        await AuditWorkflowSavedAsync(
            workflowDefinition, previousSnapshot, previousNodes, previousEdges, cancellationToken);

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

        // Captured before the delete: this is a PHYSICAL delete cascading to every node and edge, so the audit
        // row's OldValueJson is the only surviving record that this workflow ever existed.
        var snapshot = SummarizeWorkflow(existing);
        var name = existing.Name;

        // Cascade delete removes the tracked nodes/edges/configurations with the parent.
        _dbContext.WorkflowDefinitions.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await WriteAuditAsync(
            action: "Deleted",
            entityType: nameof(WorkflowDefinition),
            entityId: workflowId.ToString(),
            entityName: name,
            oldValueJson: snapshot,
            newValueJson: null,
            cancellationToken);
    }

    // ── Audit ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the workflow-level row for this save, plus one row per node/edge that actually changed.
    /// </summary>
    private async Task AuditWorkflowSavedAsync(
        WorkflowDefinition saved,
        string? previousSnapshot,
        IReadOnlyDictionary<string, string>? previousNodes,
        IReadOnlySet<string>? previousEdges,
        CancellationToken cancellationToken)
    {
        if (_governanceLogger is null)
        {
            return;
        }

        var isCreate = previousSnapshot is null;

        await WriteAuditAsync(
            action: isCreate ? "Created" : "Updated",
            entityType: nameof(WorkflowDefinition),
            entityId: saved.Id.ToString(),
            entityName: saved.Name,
            oldValueJson: previousSnapshot,
            newValueJson: SummarizeWorkflow(saved),
            cancellationToken);

        // A create already says everything in the row above; enumerating every node of a brand-new graph would
        // just restate it one row at a time.
        if (isCreate)
        {
            return;
        }

        await AuditNodeChangesAsync(saved, previousNodes!, cancellationToken);
        await AuditEdgeChangesAsync(saved, previousEdges!, cancellationToken);
    }

    /// <summary>
    /// One row per node added, removed, or whose configuration changed.
    /// <para>Diffed rather than dumped because every save is a full delete-and-re-add: without this, editing one
    /// node of a twenty-node workflow would emit forty rows and the trail would be unreadable. Keyed on
    /// <c>canvasNodeId</c> (stamped into each node's configuration by the API — see
    /// <c>WorkflowEndpoints.StampNodeIdentity</c>) rather than <c>WorkflowNode.Id</c>, because the API mints a
    /// fresh node id on every save, so id-based diffing would report every node as removed-and-re-added.</para>
    /// <para>Position is excluded from the comparison: dragging a node on the canvas is not a configuration
    /// change and must not produce a "mapping changed" row.</para>
    /// </summary>
    private async Task AuditNodeChangesAsync(
        WorkflowDefinition saved,
        IReadOnlyDictionary<string, string> previousNodes,
        CancellationToken cancellationToken)
    {
        var currentNodes = SnapshotNodes(saved);

        foreach (var (key, currentConfig) in currentNodes)
        {
            if (!previousNodes.TryGetValue(key, out var previousConfig))
            {
                await WriteNodeAuditAsync(saved, key, "Created", null, currentConfig, cancellationToken);
            }
            else if (!string.Equals(previousConfig, currentConfig, StringComparison.Ordinal))
            {
                await WriteNodeAuditAsync(saved, key, "Updated", previousConfig, currentConfig, cancellationToken);
            }
        }

        foreach (var (key, previousConfig) in previousNodes.Where(x => !currentNodes.ContainsKey(x.Key)))
        {
            await WriteNodeAuditAsync(saved, key, "Deleted", previousConfig, null, cancellationToken);
        }
    }

    /// <summary>One row per edge added or removed — an edge has no state to "update", only existence.</summary>
    private async Task AuditEdgeChangesAsync(
        WorkflowDefinition saved,
        IReadOnlySet<string> previousEdges,
        CancellationToken cancellationToken)
    {
        var currentEdges = SnapshotEdges(saved);

        foreach (var added in currentEdges.Where(edge => !previousEdges.Contains(edge)))
        {
            await WriteAuditAsync(
                "Created", nameof(WorkflowEdge), added, added, null, added, cancellationToken);
        }

        foreach (var removed in previousEdges.Where(edge => !currentEdges.Contains(edge)))
        {
            await WriteAuditAsync(
                "Deleted", nameof(WorkflowEdge), removed, removed, removed, null, cancellationToken);
        }
    }

    private Task WriteNodeAuditAsync(
        WorkflowDefinition saved,
        string canvasNodeId,
        string action,
        string? oldValueJson,
        string? newValueJson,
        CancellationToken cancellationToken)
    {
        var node = saved.Nodes.FirstOrDefault(n => ReadCanvasNodeId(n) == canvasNodeId);
        var displayName = node is null ? canvasNodeId : $"{node.DisplayName} ({node.NodeType})";

        return WriteAuditAsync(
            action,
            nameof(WorkflowNode),
            // The canvas id, not WorkflowNode.Id: it is the identity that survives a re-save, so "history of this
            // node" stays one queryable series on the (EntityType, EntityId) index rather than a new id each time.
            canvasNodeId,
            displayName,
            oldValueJson,
            newValueJson,
            cancellationToken);
    }

    /// <summary>
    /// Writes one audit row, swallowing any failure.
    /// <para>The workflow itself is already committed by the time this runs, so a governance-write failure must
    /// not surface as a failed save — the user's change succeeded. Same stance as
    /// <c>ApiRequestLoggingHandler</c>: logging is never worth failing the operation it describes.</para>
    /// </summary>
    private async Task WriteAuditAsync(
        string action,
        string entityType,
        string entityId,
        string? entityName,
        string? oldValueJson,
        string? newValueJson,
        CancellationToken cancellationToken)
    {
        if (_governanceLogger is null)
        {
            return;
        }

        try
        {
            await _governanceLogger.LogAuditAsync(
                new AuditEntry(
                    Module: nameof(WorkflowDefinition),
                    Action: action,
                    EntityType: entityType,
                    EntityId: entityId,
                    EntityName: entityName,
                    OldValueJson: oldValueJson,
                    NewValueJson: newValueJson),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not write the {Action} audit row for {EntityType} {EntityId}; the change itself succeeded.",
                action, entityType, entityId);
        }
    }

    /// <summary>The workflow's own state, without the node/edge graph (those get their own rows).</summary>
    private static string SummarizeWorkflow(WorkflowDefinition workflow) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Id"] = workflow.Id,
            ["Name"] = workflow.Name,
            ["Description"] = workflow.Description,
            ["Version"] = workflow.Version,
            ["IsEnabled"] = workflow.IsEnabled,
            ["IsPubliclyLaunchable"] = workflow.IsPubliclyLaunchable,
            ["WorkflowNumber"] = workflow.WorkflowNumber,
            ["NodeCount"] = workflow.Nodes.Count,
            ["EdgeCount"] = workflow.Edges.Count,
            ["TriggerType"] = workflow.Trigger?.Type.ToString(),
            ["ScheduleExpression"] = workflow.Trigger?.ScheduleExpression,
            ["IntervalMinutes"] = workflow.Trigger?.IntervalMinutes,
            ["TimeZoneId"] = workflow.Trigger?.TimeZoneId,
        });

    /// <summary>
    /// canvasNodeId → the node's auditable state. Position is deliberately absent: moving a node on the canvas
    /// is not a configuration change.
    /// </summary>
    private static Dictionary<string, string> SnapshotNodes(WorkflowDefinition workflow)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in workflow.Nodes)
        {
            snapshot[ReadCanvasNodeId(node)] = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["NodeType"] = node.NodeType,
                ["Category"] = node.Category.ToString(),
                ["DisplayName"] = node.DisplayName,
                ["Rank"] = node.Rank,
                ["SubRank"] = node.SubRank,
                ["IsEnabled"] = node.IsEnabled,
                ["CheckpointUrlEnabled"] = node.CheckpointUrlEnabled,
                ["ConfigurationJson"] = node.ConfigurationJson,
            });
        }

        return snapshot;
    }

    /// <summary>Edges as "fromCanvasId->toCanvasId", so they survive the id churn a re-save causes.</summary>
    private static HashSet<string> SnapshotEdges(WorkflowDefinition workflow)
    {
        var canvasIdsByNodeId = workflow.Nodes.ToDictionary(node => node.Id, ReadCanvasNodeId);

        return workflow.Edges
            .Select(edge =>
            {
                var from = canvasIdsByNodeId.TryGetValue(edge.FromNodeId, out var f) ? f : edge.FromNodeId.ToString();
                var to = canvasIdsByNodeId.TryGetValue(edge.ToNodeId, out var t) ? t : edge.ToNodeId.ToString();
                return $"{from}->{to}";
            })
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The node's stable canvas identity, stamped into its configuration by the API. Falls back to the node's own
    /// id for a graph built by something that never stamped one (e.g. the route→graph projection), which is
    /// correct in itself — it simply cannot be diffed across saves, and shows as a remove-and-add.
    /// </summary>
    private static string ReadCanvasNodeId(WorkflowNode node)
    {
        try
        {
            if (JsonNode.Parse(node.ConfigurationJson) is not JsonObject root)
            {
                return node.Id.ToString();
            }

            // Mirrors WorkflowNodeConfigurationEnvelope.ResolveSettings: an enveloped node keeps its settings
            // under "config", a bare one is its own settings object.
            var settings = root["config"] as JsonObject ?? root;
            return settings["canvasNodeId"]?.ToString() is { Length: > 0 } canvasNodeId
                ? canvasNodeId
                : node.Id.ToString();
        }
        catch (JsonException)
        {
            return node.Id.ToString();
        }
    }
}
