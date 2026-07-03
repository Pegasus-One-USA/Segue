using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IUserManagementService
{
    Task<IReadOnlyList<UserManagementDto>> GetUsersAsync(CancellationToken cancellationToken);

    Task<UserDetailDto> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserManagementDto> CreateLocalUserAsync(CreateLocalUserRequest request, CancellationToken cancellationToken);

    Task<UserManagementDto> UpdateLocalUserAsync(Guid userId, UpdateLocalUserRequest request, CancellationToken cancellationToken);

    Task<UserDetailDto> InviteUserAsync(InviteUserRequest request, CancellationToken cancellationToken);

    Task<UserDetailDto> ResendInviteAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserDetailDto> AcceptInviteAsync(AcceptInviteRequest request, CancellationToken cancellationToken);

    Task<UserDetailDto> UpdateUserStatusAsync(Guid userId, UpdateUserStatusRequest request, CancellationToken cancellationToken);

    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleDto>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserDetailDto> AssignUserRoleAsync(Guid userId, AssignUserRoleRequest request, CancellationToken cancellationToken);

    Task RemoveUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);
}
