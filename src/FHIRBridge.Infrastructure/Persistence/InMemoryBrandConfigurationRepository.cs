using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryBrandConfigurationRepository : IBrandConfigurationRepository
{
    private readonly ConcurrentDictionary<Guid, BrandConfiguration> _byTenantId = new();

    public Task<BrandConfiguration?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_byTenantId.GetValueOrDefault(tenantId));

    public Task SaveAsync(BrandConfiguration configuration, CancellationToken cancellationToken)
    {
        _byTenantId[configuration.TenantId] = configuration;
        return Task.CompletedTask;
    }
}
