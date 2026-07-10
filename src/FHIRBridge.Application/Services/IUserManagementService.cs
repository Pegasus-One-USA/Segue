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

    /// <summary>
    /// Accepts a pending invitation using an external IdP identity instead of a password. Validates the
    /// invitation token (same guards as <see cref="AcceptInviteAsync"/>) and the external token, requires
    /// the external email to equal the invite email (case-insensitive), then links the identity and
    /// activates the account. Returns the signed-in session.
    /// </summary>
    Task<LocalLoginResponse> AcceptInviteViaSsoAsync(AcceptInviteSsoRequest request, CancellationToken cancellationToken);

    Task<UserDetailDto> UpdateUserStatusAsync(Guid userId, UpdateUserStatusRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Admin override: disables MFA for a user without requiring a code — for account recovery when
    /// they've lost their authenticator and backup codes. Self-service disable (which does require a
    /// current code) is handled separately by <see cref="IMfaService.DisableAsync"/>.
    /// </summary>
    Task<UserDetailDto> DisableMfaForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Admin policy toggle: requires (or stops requiring) this account to have MFA enabled.</summary>
    Task<UserDetailDto> SetMfaRequirementAsync(Guid userId, bool required, CancellationToken cancellationToken);

    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleDto>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserDetailDto> AssignUserRoleAsync(Guid userId, AssignUserRoleRequest request, CancellationToken cancellationToken);

    Task RemoveUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionAllocationDto>> GetUserPermissionAllocationsAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserDetailDto> SetUserPermissionAllocationAsync(
        Guid userId, Guid permissionId, UpsertUserPermissionAllocationRequest request, CancellationToken cancellationToken);

    Task RemoveUserPermissionAllocationAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken);

    /// <summary>Replaces a user's entire set of direct permission overrides in one call.</summary>
    Task<UserDetailDto> SetUserPermissionAllocationsAsync(
        Guid userId, SetUserPermissionAllocationsRequest request, CancellationToken cancellationToken);
}
