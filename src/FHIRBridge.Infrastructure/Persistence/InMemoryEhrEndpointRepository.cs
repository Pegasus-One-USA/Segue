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
        EhrEndpointFilter filter, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken)
    {
        IEnumerable<EhrEndpoint> query = _store.Values.Where(x => !x.IsDeleted);

        if (filter.Vendor.HasValue)
        {
            query = query.Where(x => x.Vendor == filter.Vendor.Value);
        }

        if (filter.IsActive.HasValue)
        {
            // Same "anything that isn't 'active' is Inactive" split the EF repository and the listing itself use.
            query = query.Where(x =>
                string.Equals(x.Status, "active", StringComparison.OrdinalIgnoreCase) == filter.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(x =>
                x.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.FhirBaseUrl.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

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

    public Task<IReadOnlyList<SourceSystemType>> GetDistinctVendorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceSystemType>>(
            _store.Values
                .Where(x => !x.IsDeleted)
                .Select(x => x.Vendor)
                .Distinct()
                .OrderBy(vendor => vendor)
                .ToArray());

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
