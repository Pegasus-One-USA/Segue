using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryUserAccessRepository : IUserAccessRepository
{
    private readonly ConcurrentDictionary<Guid, User> _users = new();
    private readonly ConcurrentDictionary<Guid, Role> _roles = new(
        new[]
        {
            new Role(SeededSecurityIds.SuperAdminRoleId, UnifiedRoles.SuperAdmin, "Full platform administrator.", isSystem: true),
            new Role(SeededSecurityIds.AdminRoleId, UnifiedRoles.Admin, "Administers configuration and users.", isSystem: true),
            new Role(SeededSecurityIds.OperationsRoleId, UnifiedRoles.Operations, "Builds and runs workflow configurations, and reviews data and audit output.", isSystem: true),
            new Role(SeededSecurityIds.AuditRoleId, UnifiedRoles.Audit, "Read-only access to configuration and audit logs.", isSystem: true)
        }.ToDictionary(role => role.Id));

    private readonly ConcurrentDictionary<Guid, PermissionCategory> _permissionCategories = new(
        RbacSeedData.Categories
            .Select(c => new PermissionCategory(c.Id, c.Name, c.DisplayName))
            .ToDictionary(c => c.Id));

    private readonly ConcurrentDictionary<Guid, PermissionGroup> _permissionGroups = new(
        RbacSeedData.Groups
            .Select(g => new PermissionGroup(g.Id, g.Name, g.DisplayName, RbacSeedData.CategoryIdsByCode[g.Group.GetCategory()]))
            .ToDictionary(g => g.Id));

    private readonly ConcurrentDictionary<Guid, Permission> _permissions = new(
        RbacSeedData.Permissions
            .Select(p => new Permission(p.Id, p.Name, p.DisplayName, p.Description, RbacSeedData.GroupIdsByCode[p.Group], isSystem: true))
            .ToDictionary(p => p.Id));

    private readonly ConcurrentDictionary<string, PermissionAllocation> _rolePermissionAllocations = new();
    private readonly ConcurrentDictionary<string, PermissionAllocation> _userPermissionAllocations = new();
    private readonly ConcurrentDictionary<string, UserRole> _userRoles = new();

    public InMemoryUserAccessRepository()
    {
        foreach (var (roleId, permissionIds) in RbacSeedData.RolePermissions)
        {
            foreach (var permissionId in permissionIds)
            {
                _rolePermissionAllocations[LinkKey(roleId, permissionId)] = PermissionAllocation.ForRole(Guid.NewGuid(), roleId, permissionId);
            }
        }
    }

    // ── Users ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var users = _users.Values
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Email)
            .ThenBy(x => x.DisplayName)
            .ToList();

        return Task.FromResult<IReadOnlyList<User>>(users);
    }

    public Task<User?> GetUserByExternalIdAsync(string externalUserId, CancellationToken cancellationToken)
    {
        var user = _users.Values.FirstOrDefault(x =>
            !x.IsDeleted &&
            string.Equals(x.ExternalUserId, externalUserId, StringComparison.Ordinal));

        return Task.FromResult(user);
    }

    public Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var user = _users.Values.FirstOrDefault(x =>
            !x.IsDeleted &&
            string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(user);
    }

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        _users.TryGetValue(userId, out var user);

        return Task.FromResult(user is not null && !user.IsDeleted ? user : null);
    }

    public Task<User?> GetUserByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
    {
        var user = _users.Values.FirstOrDefault(x =>
            !x.IsDeleted &&
            string.Equals(x.RefreshTokenHash, refreshTokenHash, StringComparison.Ordinal));

        return Task.FromResult(user);
    }

    public Task<User?> GetUserByMfaChallengeTokenHashAsync(string mfaChallengeTokenHash, CancellationToken cancellationToken)
    {
        var user = _users.Values.FirstOrDefault(x =>
            !x.IsDeleted &&
            string.Equals(x.MfaChallengeTokenHash, mfaChallengeTokenHash, StringComparison.Ordinal));

        return Task.FromResult(user);
    }

    public Task AddUserAsync(User user, CancellationToken cancellationToken)
    {
        _users[user.Id] = user;

        return Task.CompletedTask;
    }

    public Task UpdateUserAsync(User user, CancellationToken cancellationToken)
    {
        _users[user.Id] = user;

        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(User user, CancellationToken cancellationToken)
    {
        user.ApplyDeleted("system", DateTime.UtcNow);
        _users[user.Id] = user;

        // Remove all role assignments for this user.
        foreach (var key in _userRoles.Keys
            .Where(k => k.StartsWith($"{user.Id:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _userRoles.TryRemove(key, out _);
        }

        // Remove all direct permission allocations for this user.
        foreach (var key in _userPermissionAllocations.Keys
            .Where(k => k.StartsWith($"{user.Id:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _userPermissionAllocations.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    // ── Roles ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken)
    {
        var roles = _roles.Values
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Role>>(roles);
    }

    public Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken cancellationToken)
    {
        var role = _roles.Values.FirstOrDefault(x =>
            !x.IsDeleted &&
            string.Equals(x.Name, roleName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(role);
    }

    public Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        _roles.TryGetValue(roleId, out var role);

        return Task.FromResult(role is not null && !role.IsDeleted ? role : null);
    }

    public Task AddRoleAsync(Role role, CancellationToken cancellationToken)
    {
        _roles[role.Id] = role;

        return Task.CompletedTask;
    }

    public Task UpdateRoleAsync(Role role, CancellationToken cancellationToken)
    {
        _roles[role.Id] = role;

        return Task.CompletedTask;
    }

    public Task DeleteRoleAsync(Role role, CancellationToken cancellationToken)
    {
        _roles.TryRemove(role.Id, out _);

        foreach (var key in _rolePermissionAllocations.Keys
            .Where(k => k.StartsWith($"{role.Id:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _rolePermissionAllocations.TryRemove(key, out _);
        }

        foreach (var key in _userRoles.Keys
            .Where(k => k.EndsWith($":{role.Id:N}", StringComparison.Ordinal))
            .ToArray())
        {
            _userRoles.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task<int> GetRoleUserCountAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var count = _userRoles.Values.Count(x => x.RoleId == roleId);

        return Task.FromResult(count);
    }

    // ── Permission Categories ────────────────────────────────────────────────

    public Task<IReadOnlyList<PermissionCategory>> GetPermissionCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = _permissionCategories.Values
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<PermissionCategory>>(categories);
    }

    public Task AddPermissionCategoryAsync(PermissionCategory category, CancellationToken cancellationToken)
    {
        _permissionCategories[category.Id] = category;

        return Task.CompletedTask;
    }

    // ── Permission Groups ────────────────────────────────────────────────────

    public Task<IReadOnlyList<PermissionGroup>> GetPermissionGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = _permissionGroups.Values
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<PermissionGroup>>(groups);
    }

    public Task AddPermissionGroupAsync(PermissionGroup group, CancellationToken cancellationToken)
    {
        _permissionGroups[group.Id] = group;

        return Task.CompletedTask;
    }

    // ── Permissions ──────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = _permissions.Values
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Permission>>(permissions);
    }

    public Task<Permission?> GetPermissionByIdAsync(Guid permissionId, CancellationToken cancellationToken)
    {
        _permissions.TryGetValue(permissionId, out var permission);

        return Task.FromResult(permission);
    }

    public Task AddPermissionAsync(Permission permission, CancellationToken cancellationToken)
    {
        _permissions[permission.Id] = permission;

        return Task.CompletedTask;
    }

    public Task UpdatePermissionAsync(Permission permission, CancellationToken cancellationToken)
    {
        _permissions[permission.Id] = permission;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Permission>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var permissions = _rolePermissionAllocations.Values
            .Where(x => x.RoleId == roleId)
            .Select(x => _permissions.TryGetValue(x.PermissionId, out var p) ? p : null)
            .OfType<Permission>()
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Permission>>(permissions);
    }

    public Task SetRolePermissionsAsync(
        Guid roleId,
        IReadOnlyCollection<Guid> permissionIds,
        CancellationToken cancellationToken)
    {
        foreach (var key in _rolePermissionAllocations.Keys
            .Where(k => k.StartsWith($"{roleId:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _rolePermissionAllocations.TryRemove(key, out _);
        }

        foreach (var permissionId in permissionIds.Distinct())
        {
            _rolePermissionAllocations[LinkKey(roleId, permissionId)] = PermissionAllocation.ForRole(Guid.NewGuid(), roleId, permissionId);
        }

        return Task.CompletedTask;
    }

    public Task AddRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        _rolePermissionAllocations[LinkKey(roleId, permissionId)] = PermissionAllocation.ForRole(Guid.NewGuid(), roleId, permissionId);

        return Task.CompletedTask;
    }

    public Task RemoveRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        _rolePermissionAllocations.TryRemove(LinkKey(roleId, permissionId), out _);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(Permission Permission, bool IsEnabled)>> GetUserPermissionAllocationsAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var allocations = _userPermissionAllocations.Values
            .Where(x => x.UserId == userId)
            .Select(x => _permissions.TryGetValue(x.PermissionId, out var p) ? (Permission: p, x.IsEnabled) : default)
            .Where(x => x.Permission is not null)
            .OrderBy(x => x.Permission!.Name)
            .Select(x => (x.Permission!, x.IsEnabled))
            .ToList();

        return Task.FromResult<IReadOnlyList<(Permission, bool)>>(allocations);
    }

    public Task SetUserPermissionAllocationsAsync(
        Guid userId,
        IReadOnlyDictionary<Guid, bool> permissionIdToIsEnabled,
        CancellationToken cancellationToken)
    {
        foreach (var key in _userPermissionAllocations.Keys
            .Where(k => k.StartsWith($"{userId:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _userPermissionAllocations.TryRemove(key, out _);
        }

        foreach (var (permissionId, isEnabled) in permissionIdToIsEnabled)
        {
            _userPermissionAllocations[LinkKey(userId, permissionId)] =
                PermissionAllocation.ForUser(Guid.NewGuid(), userId, permissionId, isEnabled);
        }

        return Task.CompletedTask;
    }

    public Task AddUserPermissionAllocationAsync(
        Guid userId, Guid permissionId, bool isEnabled, CancellationToken cancellationToken)
    {
        _userPermissionAllocations[LinkKey(userId, permissionId)] =
            PermissionAllocation.ForUser(Guid.NewGuid(), userId, permissionId, isEnabled);

        return Task.CompletedTask;
    }

    public Task RemoveUserPermissionAllocationAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken)
    {
        _userPermissionAllocations.TryRemove(LinkKey(userId, permissionId), out _);

        return Task.CompletedTask;
    }

    // ── User ↔ Role ───────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Role>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roles = _userRoles.Values
            .Where(x => x.UserId == userId)
            .Select(x => _roles.TryGetValue(x.RoleId, out var r) ? r : null)
            .OfType<Role>()
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Role>>(roles);
    }

    public Task SetUserRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        foreach (var key in _userRoles.Keys
            .Where(k => k.StartsWith($"{userId:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _userRoles.TryRemove(key, out _);
        }

        foreach (var roleId in roleIds.Distinct())
        {
            _userRoles[LinkKey(userId, roleId)] = new UserRole(userId, roleId);
        }

        return Task.CompletedTask;
    }

    public Task AddUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        _userRoles[LinkKey(userId, roleId)] = new UserRole(userId, roleId);

        return Task.CompletedTask;
    }

    public Task RemoveUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        _userRoles.TryRemove(LinkKey(userId, roleId), out _);

        return Task.CompletedTask;
    }

    private static string LinkKey(Guid leftId, Guid rightId)
    {
        return $"{leftId:N}:{rightId:N}";
    }
}
