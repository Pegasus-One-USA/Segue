using FHIRBridge.Domain.Aggregates;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface ITenantConfigurationRepository
{
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);

    Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken);

    Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken);
}
