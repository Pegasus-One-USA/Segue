using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IUserAccessRepository
{
    // ── Users ────────────────────────────────────────────────────────────────

    Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken cancellationToken);

    Task<User?> GetUserByExternalIdAsync(string externalUserId, CancellationToken cancellationToken);

    Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken);

    Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<User?> GetUserByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    Task AddUserAsync(User user, CancellationToken cancellationToken);

    Task UpdateUserAsync(User user, CancellationToken cancellationToken);

    Task DeleteUserAsync(User user, CancellationToken cancellationToken);

    // ── Roles ────────────────────────────────────────────────────────────────

    Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken);

    Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken);

    Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken cancellationToken);

    Task AddRoleAsync(Role role, CancellationToken cancellationToken);

    Task UpdateRoleAsync(Role role, CancellationToken cancellationToken);

    Task DeleteRoleAsync(Role role, CancellationToken cancellationToken);

    Task<int> GetRoleUserCountAsync(Guid roleId, CancellationToken cancellationToken);

    // ── Permission Categories ────────────────────────────────────────────────

    Task<IReadOnlyList<PermissionCategory>> GetPermissionCategoriesAsync(CancellationToken cancellationToken);

    Task AddPermissionCategoryAsync(PermissionCategory category, CancellationToken cancellationToken);

    // ── Permission Groups ────────────────────────────────────────────────────

    Task<IReadOnlyList<PermissionGroup>> GetPermissionGroupsAsync(CancellationToken cancellationToken);

    Task AddPermissionGroupAsync(PermissionGroup group, CancellationToken cancellationToken);

    // ── Permissions ──────────────────────────────────────────────────────────

    Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken);

    Task<Permission?> GetPermissionByIdAsync(Guid permissionId, CancellationToken cancellationToken);

    Task AddPermissionAsync(Permission permission, CancellationToken cancellationToken);

    Task<IReadOnlyList<Permission>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken);

    Task SetRolePermissionsAsync(Guid roleId, IReadOnlyCollection<Guid> permissionIds, CancellationToken cancellationToken);

    Task AddRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken);

    Task RemoveRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken);

    // ── User Permission Allocations (direct grant/deny overrides) ───────────

    /// <summary>The user's direct permission allocations (grant or deny overrides), each paired with its Permission.</summary>
    Task<IReadOnlyList<(Permission Permission, bool IsEnabled)>> GetUserPermissionAllocationsAsync(
        Guid userId, CancellationToken cancellationToken);

    Task SetUserPermissionAllocationsAsync(
        Guid userId, IReadOnlyDictionary<Guid, bool> permissionIdToIsEnabled, CancellationToken cancellationToken);

    Task AddUserPermissionAllocationAsync(
        Guid userId, Guid permissionId, bool isEnabled, CancellationToken cancellationToken);

    Task RemoveUserPermissionAllocationAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken);

    // ── User ↔ Role (global assignments) ────────────────────────────────────

    Task<IReadOnlyList<Role>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken);

    Task SetUserRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);

    Task AddUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);

    Task RemoveUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);
}
