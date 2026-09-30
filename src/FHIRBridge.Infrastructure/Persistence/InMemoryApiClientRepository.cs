using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryApiClientRepository : IApiClientRepository
{
    private readonly ConcurrentDictionary<Guid, ApiClient> _store = new();

    public Task<PagedResult<ApiClient>> GetPagedAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        IEnumerable<ApiClient> query = _store.Values;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                x.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.ClientId.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = query.OrderBy(x => x.Name).ToArray();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return Task.FromResult(new PagedResult<ApiClient>(items, ordered.Length, page, pageSize));
    }

    public Task<ApiClient?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        _store.TryGetValue(id, out var client);
        return Task.FromResult(client);
    }

    public Task<ApiClient?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(_store.Values.FirstOrDefault(x => x.ClientId == clientId));

    public Task<bool> ReturnUrlExistsAsync(Guid apiClientId, string url, CancellationToken cancellationToken)
    {
        _store.TryGetValue(apiClientId, out var client);
        var exists = client?.ReturnUrls.Any(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)) ?? false;
        return Task.FromResult(exists);
    }

    public Task AddAsync(ApiClient client, CancellationToken cancellationToken)
    {
        _store[client.Id] = client;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ApiClient client, CancellationToken cancellationToken)
    {
        _store[client.Id] = client;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ApiClient client, CancellationToken cancellationToken)
    {
        _store.TryRemove(client.Id, out _);
        return Task.CompletedTask;
    }
}
