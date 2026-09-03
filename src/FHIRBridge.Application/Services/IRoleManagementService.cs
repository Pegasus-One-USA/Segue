using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IRoleManagementService
{
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken);

    /// <summary>Server-side paged/search listing backing the admin Role Management screen's table. Roles are a
    /// small dataset (system + tenant custom roles), so unlike the EhrEndpoint/Tenant equivalents this pages the
    /// already-materialized <see cref="GetRolesAsync"/> result in memory rather than pushing paging to the
    /// repository — <see cref="GetRolesAsync"/> stays used unchanged by pickers/dropdowns elsewhere that need
    /// every role. <paramref name="search"/> is an optional case-insensitive contains-match on Name or
    /// Description; <paramref name="sortDescending"/> orders by the "action on" timestamp (ModifiedOnUtc, falling
    /// back to CreatedOnUtc) — null keeps the default Name ordering.</summary>
    Task<PagedResult<RoleDto>> GetPagedRolesAsync(
        string? search, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken);

    Task<RoleDto> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionCatalogCategoryDto>> GetPermissionCatalogAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDto>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken);

    Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken);

    Task<RoleDto> UpdateRoleAsync(Guid roleId, UpdateRoleRequest request, CancellationToken cancellationToken);

    Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken);

    Task<RoleDto> AddRolePermissionsAsync(Guid roleId, AddRolePermissionsRequest request, CancellationToken cancellationToken);

    Task RemoveRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken);
}
