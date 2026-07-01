using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Persistence for per-source <see cref="SourceCapabilityProfile"/> snapshots. Kept separate from the tenant
/// aggregate repository so capability discovery can read/refresh snapshots without loading the whole tenant.
/// </summary>
public interface ISourceCapabilityRepository
{
    Task<SourceCapabilityProfile?> GetBySourceConnectionIdAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken);

    /// <summary>Inserts the snapshot, or replaces the existing snapshot for the same source connection.</summary>
    Task UpsertAsync(SourceCapabilityProfile profile, CancellationToken cancellationToken);
}
