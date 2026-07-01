using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IUserAccessRepository
{
    Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken cancellationToken);

    Task<User?> GetUserByExternalIdAsync(string externalUserId, CancellationToken cancellationToken);

    Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken);

    Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task AddUserAsync(User user, CancellationToken cancellationToken);

    Task UpdateUserAsync(User user, CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken);

    Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken);

    Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken cancellationToken);

    Task AddRoleAsync(Role role, CancellationToken cancellationToken);

    Task UpdateRoleAsync(Role role, CancellationToken cancellationToken);

    Task DeleteRoleAsync(Role role, CancellationToken cancellationToken);

    Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Permission>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken);

    Task SetRolePermissionsAsync(Guid roleId, IReadOnlyCollection<Guid> permissionIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken);

    Task SetUserRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantUser>> GetTenantUsersAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantUser>> GetTenantMembershipsByUserIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<TenantUser?> GetTenantUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    Task AddTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken);

    Task UpdateTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken);

    Task<bool> HasTenantRoleAsync(
        Guid tenantId,
        string externalUserId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken);
}
