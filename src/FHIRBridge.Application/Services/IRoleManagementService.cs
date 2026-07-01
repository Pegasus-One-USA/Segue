using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IRoleManagementService
{
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken);

    Task<RoleDto> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDto>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken);

    Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken);

    Task<RoleDto> UpdateRoleAsync(Guid roleId, UpdateRoleRequest request, CancellationToken cancellationToken);

    Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken);

    Task<RoleDto> AddRolePermissionsAsync(Guid roleId, AddRolePermissionsRequest request, CancellationToken cancellationToken);

    Task RemoveRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken);
}
