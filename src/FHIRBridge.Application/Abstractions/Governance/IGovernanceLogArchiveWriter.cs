namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Serializes expiring governance-log rows to a durable artifact before <see cref="IPurgeableStore"/> deletes
/// them, and records what was archived (where, how many rows, through what cutoff) — see <c>ArchiveManifestEntry</c>.
/// </summary>
public interface IGovernanceLogArchiveWriter
{
    Task ArchiveAsync<TEntity>(
        string dataClass, IReadOnlyList<TEntity> rows, DateTime cutoffUtc, CancellationToken cancellationToken);
}
