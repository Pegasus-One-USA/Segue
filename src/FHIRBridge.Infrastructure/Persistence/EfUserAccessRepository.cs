using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfUserAccessRepository : IUserAccessRepository
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfUserAccessRepository(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Users
            .OrderBy(x => x.Email)
            .ThenBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public Task<User?> GetUserByExternalIdAsync(string externalUserId, CancellationToken cancellationToken)
    {
        return _dbContext.Users.FirstOrDefaultAsync(
            x => x.ExternalUserId == externalUserId,
            cancellationToken);
    }

    public Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        return _dbContext.Users.FirstOrDefaultAsync(
            x => x.Email != null && x.Email == email,
            cancellationToken);
    }

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
    }

    public async Task AddUserAsync(User user, CancellationToken cancellationToken)
    {
        await _dbContext.Users.AddAsync(user, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateUserAsync(User user, CancellationToken cancellationToken)
    {
        _dbContext.Users.Update(user);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<User?> GetUserByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
    {
        return _dbContext.Users.FirstOrDefaultAsync(
            x => x.RefreshTokenHash != null && x.RefreshTokenHash == refreshTokenHash,
            cancellationToken);
    }

    public async Task<IReadOnlyList<User>> GetUsersByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        return await _dbContext.Users
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Email)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(User user, CancellationToken cancellationToken)
    {
        user.ApplyDeleted("system", DateTime.UtcNow);
        _dbContext.Users.Update(user);

        var userRoles = await _dbContext.UserRoles
            .Where(x => x.UserId == user.Id)
            .ToListAsync(cancellationToken);

        _dbContext.UserRoles.RemoveRange(userRoles);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken cancellationToken)
    {
        return _dbContext.Roles.FirstOrDefaultAsync(x => x.Name == roleName, cancellationToken);
    }

    public async Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Roles
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return _dbContext.Roles.FirstOrDefaultAsync(x => x.Id == roleId, cancellationToken);
    }

    public async Task AddRoleAsync(Role role, CancellationToken cancellationToken)
    {
        await _dbContext.Roles.AddAsync(role, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRoleAsync(Role role, CancellationToken cancellationToken)
    {
        _dbContext.Roles.Update(role);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRoleAsync(Role role, CancellationToken cancellationToken)
    {
        _dbContext.Roles.Remove(role);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Role>> GetRolesByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        return await _dbContext.Roles
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<int> GetRoleUserCountAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return _dbContext.UserRoles
            .Where(x => x.RoleId == roleId)
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Permissions
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<Permission?> GetPermissionByIdAsync(Guid permissionId, CancellationToken cancellationToken)
    {
        return _dbContext.Permissions.FirstOrDefaultAsync(x => x.Id == permissionId, cancellationToken);
    }

    public async Task AddPermissionAsync(Permission permission, CancellationToken cancellationToken)
    {
        await _dbContext.Permissions.AddAsync(permission, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Permission>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return await _dbContext.RolePermissions
            .Where(x => x.RoleId == roleId)
            .Join(
                _dbContext.Permissions,
                rolePermission => rolePermission.PermissionId,
                permission => permission.Id,
                (_, permission) => permission)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task SetRolePermissionsAsync(
        Guid roleId,
        IReadOnlyCollection<Guid> permissionIds,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.RolePermissions
            .Where(x => x.RoleId == roleId)
            .ToListAsync(cancellationToken);

        _dbContext.RolePermissions.RemoveRange(existing);
        await _dbContext.RolePermissions.AddRangeAsync(
            permissionIds.Distinct().Select(permissionId => new RolePermission(roleId, permissionId)),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.RolePermissions
            .AnyAsync(x => x.RoleId == roleId && x.PermissionId == permissionId, cancellationToken);

        if (!exists)
        {
            await _dbContext.RolePermissions.AddAsync(new RolePermission(roleId, permissionId), cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RemoveRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        var link = await _dbContext.RolePermissions
            .FirstOrDefaultAsync(x => x.RoleId == roleId && x.PermissionId == permissionId, cancellationToken);

        if (link is not null)
        {
            _dbContext.RolePermissions.Remove(link);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Role>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.UserRoles
            .Where(x => x.UserId == userId)
            .Join(
                _dbContext.Roles,
                userRole => userRole.RoleId,
                role => role.Id,
                (_, role) => role)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task SetUserRolesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.UserRoles
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);

        _dbContext.UserRoles.RemoveRange(existing);
        await _dbContext.UserRoles.AddRangeAsync(
            roleIds.Distinct().Select(roleId => new UserRole(userId, roleId)),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.UserRoles
            .AnyAsync(x => x.UserId == userId && x.RoleId == roleId, cancellationToken);

        if (!exists)
        {
            await _dbContext.UserRoles.AddAsync(new UserRole(userId, roleId), cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RemoveUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var link = await _dbContext.UserRoles
            .FirstOrDefaultAsync(x => x.UserId == userId && x.RoleId == roleId, cancellationToken);

        if (link is not null)
        {
            _dbContext.UserRoles.Remove(link);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TenantUser>> GetTenantUsersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        return await _dbContext.TenantUsers
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.CreatedOnUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantUser>> GetTenantMembershipsByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.TenantUsers
            .Where(x => x.UserId == userId && x.IsEnabled)
            .OrderBy(x => x.CreatedOnUtc)
            .ToListAsync(cancellationToken);
    }

    public Task<TenantUser?> GetTenantUserAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return _dbContext.TenantUsers.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.UserId == userId,
            cancellationToken);
    }

    public async Task AddTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken)
    {
        await _dbContext.TenantUsers.AddAsync(tenantUser, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken)
    {
        _dbContext.TenantUsers.Update(tenantUser);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> HasTenantRoleAsync(
        Guid tenantId,
        string externalUserId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        return _dbContext.TenantUsers
            .Where(x => x.TenantId == tenantId && x.IsEnabled)
            .Join(_dbContext.Users,
                tenantUser => tenantUser.UserId,
                user => user.Id,
                (tenantUser, user) => new { tenantUser, user })
            .Join(_dbContext.Roles,
                joined => joined.tenantUser.RoleId,
                role => role.Id,
                (joined, role) => new { joined.user, role })
            .AnyAsync(
                x => x.user.ExternalUserId == externalUserId && roleNames.Contains(x.role.Name),
                cancellationToken);
    }

    public Task<bool> TenantHasSuperAdminAsync(Guid tenantId, Guid superAdminRoleId, CancellationToken cancellationToken)
    {
        return _dbContext.TenantUsers
            .AnyAsync(
                x => x.TenantId == tenantId && x.IsEnabled && x.RoleId == superAdminRoleId,
                cancellationToken);
    }
}
