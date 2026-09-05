using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Tenancy;

/// <summary>Tenant CRUD — the real backend for the portal's Tenant Management screens. SuperAdmin-only
/// (see TenantsController); tenant scoping/isolation for branding/users lives elsewhere
/// (ICurrentTenantResolver, BrandConfigurationService) — this service manages the Tenant rows themselves.</summary>
public interface ITenantsService
{
    Task<IReadOnlyList<TenantDto>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Server-side paged/search listing backing the admin Tenant Management screen's table — unlike
    /// <see cref="GetAllAsync"/>, which stays used by pickers/dropdowns elsewhere that need every tenant.</summary>
    Task<PagedResult<TenantDto>> GetPagedAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<TenantDto> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<TenantDto> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken);

    Task<TenantDto> UpdateAsync(Guid id, UpdateTenantRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
