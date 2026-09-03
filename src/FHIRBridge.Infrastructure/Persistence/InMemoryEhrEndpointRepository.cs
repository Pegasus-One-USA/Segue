using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Used when no database connection string is configured (dev only). IEhrEndpointDirectorySeeder implementations
/// only run against a real database (see DependencyInjection), so this only ever holds rows added by hand.
/// </summary>
public sealed class InMemoryEhrEndpointRepository : IEhrEndpointRepository
{
    private readonly ConcurrentDictionary<Guid, EhrEndpoint> _store = new();

    public Task<IReadOnlyList<EhrEndpoint>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EhrEndpoint>>(
            _store.Values.Where(x => !x.IsDeleted).OrderBy(x => x.Name).ToArray());

    public Task<PagedResult<EhrEndpoint>> GetPagedAsync(
        string? search, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken)
    {
        IEnumerable<EhrEndpoint> query = _store.Values
            .Where(x => !x.IsDeleted)
            .Where(x => string.IsNullOrWhiteSpace(search)
                || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.FhirBaseUrl.Contains(search, StringComparison.OrdinalIgnoreCase));

        query = sortDescending switch
        {
            true => query.OrderByDescending(x => x.ModifiedOnUtc ?? x.CreatedOnUtc),
            false => query.OrderBy(x => x.ModifiedOnUtc ?? x.CreatedOnUtc),
            null => query.OrderBy(x => x.Name),
        };

        var all = query.ToArray();
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return Task.FromResult(new PagedResult<EhrEndpoint>(items, all.Length, page, pageSize));
    }

    public Task<IReadOnlyList<EhrEndpoint>> GetPublicAsync(
        EhrEndpointType endpointType, string? search, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EhrEndpoint>>(
            _store.Values
                .Where(x => !x.IsDeleted)
                .Where(x => x.EndpointType == endpointType)
                .Where(x => string.IsNullOrWhiteSpace(search) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Name)
                .ToArray());

    public Task<EhrEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        _store.TryGetValue(id, out var endpoint);
        return Task.FromResult(endpoint is not null && !endpoint.IsDeleted ? endpoint : null);
    }

    public Task AddAsync(EhrEndpoint endpoint, CancellationToken cancellationToken)
    {
        _store[endpoint.Id] = endpoint;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(EhrEndpoint endpoint, CancellationToken cancellationToken)
    {
        _store[endpoint.Id] = endpoint;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(EhrEndpoint endpoint, CancellationToken cancellationToken)
    {
        endpoint.ApplyDeleted("system", DateTime.UtcNow);
        _store[endpoint.Id] = endpoint;
        return Task.CompletedTask;
    }
}
