using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Persistence for <see cref="BrandConfiguration"/> — at most one row per tenant (see the unique index on
/// TenantId in BrandConfigurationConfig).
/// </summary>
public interface IBrandConfigurationRepository
{
    /// <summary>Returns the given tenant's branding row, or null if that tenant has never saved one
    /// (the portal shows built-in defaults in that case).</summary>
    Task<BrandConfiguration?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Inserts the row on first save for that tenant; updates the existing one on every save after.</summary>
    Task SaveAsync(BrandConfiguration configuration, CancellationToken cancellationToken);
}
