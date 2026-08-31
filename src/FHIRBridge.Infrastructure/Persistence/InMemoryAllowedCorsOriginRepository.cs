using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryAllowedCorsOriginRepository : IAllowedCorsOriginRepository
{
    private readonly ConcurrentDictionary<Guid, AllowedCorsOrigin> _store = new();

    public Task<IReadOnlyList<AllowedCorsOrigin>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AllowedCorsOrigin>>(
            _store.Values.OrderBy(x => x.OriginUrl).ToArray());

    public Task<AllowedCorsOrigin?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        _store.TryGetValue(id, out var origin);
        return Task.FromResult(origin);
    }

    public Task<bool> ExistsAsync(string originUrl, CancellationToken cancellationToken) =>
        Task.FromResult(_store.Values.Any(x => string.Equals(x.OriginUrl, originUrl, StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken)
    {
        _store[origin.Id] = origin;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken)
    {
        _store[origin.Id] = origin;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken)
    {
        _store.TryRemove(origin.Id, out _);
        return Task.CompletedTask;
    }
}
