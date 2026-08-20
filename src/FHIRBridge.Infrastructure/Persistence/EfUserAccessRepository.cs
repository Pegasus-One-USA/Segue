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

    public Task<User?> GetUserByMfaChallengeTokenHashAsync(string mfaChallengeTokenHash, CancellationToken cancellationToken)
    {
        return _dbContext.Users.FirstOrDefaultAsync(
            x => x.MfaChallengeTokenHash != null && x.MfaChallengeTokenHash == mfaChallengeTokenHash,
            cancellationToken);
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

        // PermissionAllocation is ISoftDeletable, so a tracked Remove() would be converted to a soft
        // delete by AuditingSaveChangesInterceptor — which would leave the row occupying the
        // (UserId, PermissionId) unique index and break any future re-grant for this user. Use
        // ExecuteDeleteAsync to bypass the change tracker (and interceptor) for a genuine hard delete.
        await _dbContext.PermissionAllocations
            .Where(x => x.UserId == user.Id)
            .ExecuteDeleteAsync(cancellationToken);
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

    public Task<int> GetRoleUserCountAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return _dbContext.UserRoles
            .Where(x => x.RoleId == roleId)
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionCategory>> GetPermissionCategoriesAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.PermissionCategories
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddPermissionCategoryAsync(PermissionCategory category, CancellationToken cancellationToken)
    {
        await _dbContext.PermissionCategories.AddAsync(category, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionGroup>> GetPermissionGroupsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.PermissionGroups
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddPermissionGroupAsync(PermissionGroup group, CancellationToken cancellationToken)
    {
        await _dbContext.PermissionGroups.AddAsync(group, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
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

    public async Task UpdatePermissionAsync(Permission permission, CancellationToken cancellationToken)
    {
        _dbContext.Permissions.Update(permission);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Permission>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return await _dbContext.PermissionAllocations
            .Where(x => x.RoleId == roleId)
            .Join(
                _dbContext.Permissions,
                allocation => allocation.PermissionId,
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
        // ExecuteDeleteAsync bypasses the change tracker (and AuditingSaveChangesInterceptor's
        // soft-delete conversion for ISoftDeletable entities) — see DeleteUserAsync for why a
        // genuine hard delete is required here. IgnoreQueryFilters() matters too: the global
        // soft-delete filter (!IsDeleted) would otherwise narrow this delete to only currently-active
        // rows, silently leaving any already-soft-deleted row for this role behind — the unique
        // (RoleId, PermissionId) index has no IsDeleted-aware filter, so the AddRangeAsync below would
        // then throw a duplicate-key DbUpdateException the moment the desired set re-includes that
        // exact permission id.
        await _dbContext.PermissionAllocations
            .IgnoreQueryFilters()
            .Where(x => x.RoleId == roleId)
            .ExecuteDeleteAsync(cancellationToken);

        await _dbContext.PermissionAllocations.AddRangeAsync(
            permissionIds.Distinct().Select(permissionId => PermissionAllocation.ForRole(Guid.NewGuid(), roleId, permissionId)),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters(): same reasoning as SetRolePermissionsAsync above — a soft-deleted row for
        // this exact (RoleId, PermissionId) would otherwise be invisible to this check (the global
        // !IsDeleted filter hides it), so `exists` would wrongly come back false and the AddAsync below
        // would collide with it on the unique index instead of correctly no-op'ing.
        var exists = await _dbContext.PermissionAllocations
            .IgnoreQueryFilters()
            .AnyAsync(x => x.RoleId == roleId && x.PermissionId == permissionId, cancellationToken);

        if (!exists)
        {
            await _dbContext.PermissionAllocations.AddAsync(PermissionAllocation.ForRole(Guid.NewGuid(), roleId, permissionId), cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RemoveRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        await _dbContext.PermissionAllocations
            .Where(x => x.RoleId == roleId && x.PermissionId == permissionId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(Permission Permission, bool IsEnabled)>> GetUserPermissionAllocationsAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.PermissionAllocations
            .Where(x => x.UserId == userId)
            .Join(
                _dbContext.Permissions,
                allocation => allocation.PermissionId,
                permission => permission.Id,
                (allocation, permission) => new { allocation.IsEnabled, Permission = permission })
            .OrderBy(x => x.Permission.Name)
            .ToListAsync(cancellationToken);

        return rows.Select(x => (x.Permission, x.IsEnabled)).ToArray();
    }

    public async Task SetUserPermissionAllocationsAsync(
        Guid userId,
        IReadOnlyDictionary<Guid, bool> permissionIdToIsEnabled,
        CancellationToken cancellationToken)
    {
        await _dbContext.PermissionAllocations
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await _dbContext.PermissionAllocations.AddRangeAsync(
            permissionIdToIsEnabled.Select(kv => PermissionAllocation.ForUser(Guid.NewGuid(), userId, kv.Key, kv.Value)),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddUserPermissionAllocationAsync(
        Guid userId, Guid permissionId, bool isEnabled, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.PermissionAllocations
            .FirstOrDefaultAsync(x => x.UserId == userId && x.PermissionId == permissionId, cancellationToken);

        if (existing is not null)
        {
            existing.SetEnabled(isEnabled);
        }
        else
        {
            await _dbContext.PermissionAllocations.AddAsync(
                PermissionAllocation.ForUser(Guid.NewGuid(), userId, permissionId, isEnabled), cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveUserPermissionAllocationAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken)
    {
        await _dbContext.PermissionAllocations
            .Where(x => x.UserId == userId && x.PermissionId == permissionId)
            .ExecuteDeleteAsync(cancellationToken);
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
}
