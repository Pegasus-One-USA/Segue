using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>In-memory capability store used when no database connection string is configured (dev only).</summary>
public sealed class InMemorySourceCapabilityRepository : ISourceCapabilityRepository
{
    private readonly ConcurrentDictionary<Guid, SourceCapabilityProfile> _store = new();

    public Task<SourceCapabilityProfile?> GetBySourceConnectionIdAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        _store.TryGetValue(sourceConnectionId, out var profile);

        return Task.FromResult(profile);
    }

    public Task UpsertAsync(SourceCapabilityProfile profile, CancellationToken cancellationToken)
    {
        _store[profile.SourceConnectionId] = profile;

        return Task.CompletedTask;
    }
}
