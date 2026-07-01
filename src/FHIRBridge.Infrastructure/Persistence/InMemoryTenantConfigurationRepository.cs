using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Aggregates;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryTenantConfigurationRepository : ITenantConfigurationRepository
{
    private readonly ConcurrentDictionary<Guid, Tenant> _tenants = new();

    public Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _tenants[tenant.Id] = tenant;

        return Task.CompletedTask;
    }

    public Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        _tenants.TryGetValue(tenantId, out var tenant);

        return Task.FromResult(tenant);
    }

    public Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken)
    {
        var tenants = _tenants.Values
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Tenant>>(tenants);
    }

    public Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _tenants[tenant.Id] = tenant;

        return Task.CompletedTask;
    }
}
