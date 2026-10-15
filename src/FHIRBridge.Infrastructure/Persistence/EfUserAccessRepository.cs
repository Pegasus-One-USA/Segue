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
        await InTransactionAsync(async () =>
        {
            user.ApplyDeleted("system", DateTime.UtcNow);
            _dbContext.Users.Update(user);

            var userRoles = await _dbContext.UserRoles
                .Where(x => x.UserId == user.Id)
                .ToListAsync(cancellationToken);
            _dbContext.UserRoles.RemoveRange(userRoles);

            await _dbContext.SaveChangesAsync(cancellationToken);

            var allocations = await _dbContext.PermissionAllocations
                .IgnoreQueryFilters()
                .Where(x => x.UserId == user.Id)
                .ToListAsync(cancellationToken);
            await RevokeAllocationsAsync(allocations, cancellationToken);
        }, cancellationToken);
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
        // Applies only the difference: revokes what is no longer wanted, grants what is new, re-enables a kept
        // grant that was disabled, and leaves every other row (and its id) untouched. Replacing the whole set made
        // one added permission look like the role's every permission being created again, and recorded none of
        // the removals. IgnoreQueryFilters(): a row soft-deleted before revocations became physical still occupies
        // the (RoleId, PermissionId) unique index, so it is cleared out as well before any re-grant.
        var desired = permissionIds.ToHashSet();

        await InTransactionAsync(async () =>
        {
            var existing = await _dbContext.PermissionAllocations
                .IgnoreQueryFilters()
                .Where(x => x.RoleId == roleId)
                .ToListAsync(cancellationToken);

            await RevokeAllocationsAsync(
                existing.Where(x => x.IsDeleted || !desired.Contains(x.PermissionId)).ToList(),
                cancellationToken);

            var kept = existing.Where(x => !x.IsDeleted && desired.Contains(x.PermissionId)).ToList();
            foreach (var allocation in kept.Where(x => !x.IsEnabled))
            {
                allocation.SetEnabled(true);
            }

            var keptPermissionIds = kept.Select(x => x.PermissionId).ToHashSet();
            await _dbContext.PermissionAllocations.AddRangeAsync(
                desired.Where(id => !keptPermissionIds.Contains(id))
                    .Select(permissionId => PermissionAllocation.ForRole(Guid.NewGuid(), roleId, permissionId)),
                cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
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
        await InTransactionAsync(async () =>
        {
            var allocations = await _dbContext.PermissionAllocations
                .IgnoreQueryFilters()
                .Where(x => x.RoleId == roleId && x.PermissionId == permissionId)
                .ToListAsync(cancellationToken);
            await RevokeAllocationsAsync(allocations, cancellationToken);
        }, cancellationToken);
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
        // Same difference-only approach as SetRolePermissionsAsync: a changed grant/deny flag is an update of the
        // existing row, not a delete and re-create.
        await InTransactionAsync(async () =>
        {
            var existing = await _dbContext.PermissionAllocations
                .IgnoreQueryFilters()
                .Where(x => x.UserId == userId)
                .ToListAsync(cancellationToken);

            await RevokeAllocationsAsync(
                existing.Where(x => x.IsDeleted || !permissionIdToIsEnabled.ContainsKey(x.PermissionId)).ToList(),
                cancellationToken);

            var kept = existing.Where(x => !x.IsDeleted && permissionIdToIsEnabled.ContainsKey(x.PermissionId)).ToList();
            foreach (var allocation in kept)
            {
                var isEnabled = permissionIdToIsEnabled[allocation.PermissionId];
                if (allocation.IsEnabled != isEnabled)
                {
                    allocation.SetEnabled(isEnabled);
                }
            }

            var keptPermissionIds = kept.Select(x => x.PermissionId).ToHashSet();
            await _dbContext.PermissionAllocations.AddRangeAsync(
                permissionIdToIsEnabled.Where(kv => !keptPermissionIds.Contains(kv.Key))
                    .Select(kv => PermissionAllocation.ForUser(Guid.NewGuid(), userId, kv.Key, kv.Value)),
                cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
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
        await InTransactionAsync(async () =>
        {
            var allocations = await _dbContext.PermissionAllocations
                .IgnoreQueryFilters()
                .Where(x => x.UserId == userId && x.PermissionId == permissionId)
                .ToListAsync(cancellationToken);
            await RevokeAllocationsAsync(allocations, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Revokes the given tracked allocations and saves. <see cref="PermissionAllocation"/> is
    /// <see cref="FHIRBridge.SharedKernel.Abstractions.IHardDeletable"/>, so each <c>Remove()</c> is a real DELETE (a
    /// soft-deleted row would keep occupying the unique index and block a re-grant) and
    /// <c>AuditingSaveChangesInterceptor</c> writes its "Deleted" audit row in this same save. Saved on its own,
    /// before any grant, so a re-grant never meets the row it replaces on the unique index.
    /// </summary>
    private async Task RevokeAllocationsAsync(
        IReadOnlyCollection<PermissionAllocation> allocations, CancellationToken cancellationToken)
    {
        if (allocations.Count == 0)
        {
            return;
        }

        _dbContext.PermissionAllocations.RemoveRange(allocations);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Runs the work in one database transaction, so a permission change made in more than one save is
    /// applied completely or not at all. Joins a transaction that is already open; runs as-is on a non-relational
    /// provider (the in-memory store used by tests has no transactions).
    /// <para>Runs through the context's execution strategy: a retrying strategy (EnableRetryOnFailure, common on Azure
    /// SQL) rejects a transaction started outside it, and may run the whole unit again after a transient failure.
    /// Each attempt therefore starts with no permission allocations tracked, and re-reads them; the work computes
    /// its changes from what it reads.</para></summary>
    private async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational() || _dbContext.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            foreach (var entry in _dbContext.ChangeTracker.Entries<PermissionAllocation>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await work();
            await transaction.CommitAsync(cancellationToken);
        });
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
