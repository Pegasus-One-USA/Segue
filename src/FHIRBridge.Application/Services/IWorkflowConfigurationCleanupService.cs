using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Retires the configuration a workflow save just stopped referencing — the mapping profiles and
/// workflow-scoped transformation rules left behind when a destination (and the Mapping / Transformation /
/// De-identification steps feeding it) is removed from the canvas.
/// </summary>
/// <remarks>
/// Nothing here issues a physical DELETE. Both record types are <c>ISoftDeletable</c>, so the removes
/// below become <c>IsDeleted</c>/<c>DeletedOnUtc</c>/<c>DeletedBy</c> stamps via
/// <c>AuditingSaveChangesInterceptor</c>, and the global query filter
/// (<c>FHIRBridgeDbContext.ApplySoftDeleteFilter</c>) hides them from every normal read while leaving the
/// rows — and every audit/lineage reference to their ids — resolvable.
/// </remarks>
public interface IWorkflowConfigurationCleanupService
{
    /// <summary>
    /// Soft-deletes what <paramref name="workflowId"/> referenced before this save and no longer does.
    /// Call it as part of the same save, with the PRE-save definition and the POST-save node
    /// configuration, so "still referenced" is judged against what is actually being persisted.
    /// </summary>
    /// <param name="previousDefinition">
    /// The stored definition being replaced. Null for a brand-new workflow, which has nothing to retire.
    /// </param>
    /// <param name="newNodeConfigurationJson">
    /// Every node's <c>ConfigurationJson</c> from the incoming save.
    /// </param>
    Task<WorkflowConfigurationCleanupResult> SoftDeleteUnreferencedAsync(
        Guid workflowId,
        WorkflowDefinition? previousDefinition,
        IReadOnlyCollection<string?> newNodeConfigurationJson,
        CancellationToken cancellationToken);
}

/// <summary>What one cleanup pass retired. Ids, so a caller can report or log the change.</summary>
public sealed record WorkflowConfigurationCleanupResult(
    IReadOnlyCollection<Guid> MappingProfileIds,
    IReadOnlyCollection<Guid> TransformationRuleIds)
{
    public static WorkflowConfigurationCleanupResult Empty { get; } = new([], []);

    public bool RetiredAnything => MappingProfileIds.Count > 0 || TransformationRuleIds.Count > 0;
}
