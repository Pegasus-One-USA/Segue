namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Keeps an interactive source connection's persisted OAuth scopes derived from what its pipelines actually consume
/// (the union of every destination's selected resource types, across every workflow whose source node references
/// this connection) rather than a value an admin can hand-edit and let drift out of sync — the direct cause of a
/// source connection requesting more (or less) than its pipelines really need.
/// </summary>
public interface IEpicSourceConnectionScopeSyncService
{
    /// <summary>
    /// Recomputes and persists the connection's scopes. A no-op for connections with no <c>Interactive</c>
    /// configuration (Backend Services sources aren't resource-picker driven the same way). Returns the recomputed
    /// scope list, or null if the connection doesn't apply or wasn't found.
    /// </summary>
    Task<IReadOnlyList<string>?> SyncAsync(Guid sourceConnectionId, CancellationToken cancellationToken);

    /// <summary>Runs <see cref="SyncAsync"/> for every interactive source connection. Used for one-time backfill
    /// after this sync behavior is introduced, to correct any connections that already drifted under the old
    /// last-save-wins behavior.</summary>
    Task<IReadOnlyList<Guid>> SyncAllAsync(CancellationToken cancellationToken);
}
