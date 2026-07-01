using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IRoleManagementService
{
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken);

    Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken);

    Task<RoleDto> UpdateRoleAsync(Guid roleId, UpdateRoleRequest request, CancellationToken cancellationToken);

    Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken);
}
