using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryUserAccessRepository : IUserAccessRepository
{
    private readonly ConcurrentDictionary<Guid, User> _users = new();
    private readonly ConcurrentDictionary<Guid, TenantUser> _tenantUsers = new();
    private readonly ConcurrentDictionary<Guid, Role> _roles = new(
        new[]
        {
            new Role(SeededSecurityIds.GlobalAdminRoleId, UnifiedRoles.GlobalAdmin, "Full platform administrator across all tenants."),
            new Role(SeededSecurityIds.TenantAdminRoleId, UnifiedRoles.TenantAdmin, "Administers configuration and users within a tenant."),
            new Role(SeededSecurityIds.PipelineEngineerRoleId, UnifiedRoles.PipelineEngineer, "Builds and runs pipeline configurations within a tenant."),
            new Role(SeededSecurityIds.AnalystRoleId, UnifiedRoles.Analyst, "Runs pipelines and reviews data and audit output."),
            new Role(SeededSecurityIds.AuditorRoleId, UnifiedRoles.Auditor, "Read-only access to configuration and audit logs.")
        }.ToDictionary(role => role.Id));
    private readonly ConcurrentDictionary<Guid, Permission> _permissions = new(
        new[]
        {
            new Permission(SeededSecurityIds.TenantsReadPermissionId, UnifiedPermissions.TenantsRead, "Read tenant configuration."),
            new Permission(SeededSecurityIds.TenantsWritePermissionId, UnifiedPermissions.TenantsWrite, "Create and update tenants."),
            new Permission(SeededSecurityIds.ConfigurationWritePermissionId, UnifiedPermissions.ConfigurationWrite, "Manage source, destination, mapping, webhook, and route configuration."),
            new Permission(SeededSecurityIds.PipelineExecutePermissionId, UnifiedPermissions.PipelineExecute, "Execute configured pipeline routes."),
            new Permission(SeededSecurityIds.AuditLogsReadPermissionId, UnifiedPermissions.AuditLogsRead, "Read operational audit logs."),
            new Permission(SeededSecurityIds.SourceConnectionsTestPermissionId, UnifiedPermissions.SourceConnectionsTest, "Test source system connectivity.")
        }.ToDictionary(permission => permission.Id));
    private readonly ConcurrentDictionary<string, RolePermission> _rolePermissions = new();
    private readonly ConcurrentDictionary<string, UserRole> _userRoles = new();

    public InMemoryUserAccessRepository()
    {
        foreach (var (roleId, permissionIds) in UnifiedRolePermissionSeed.Grants)
        {
            foreach (var permissionId in permissionIds)
            {
                _rolePermissions[LinkKey(roleId, permissionId)] = new RolePermission(roleId, permissionId);
            }
        }
    }

    public Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var users = _users.Values
            .OrderBy(x => x.Email)
            .ThenBy(x => x.DisplayName)
            .ToList();

        return Task.FromResult<IReadOnlyList<User>>(users);
    }

    public Task<User?> GetUserByExternalIdAsync(string externalUserId, CancellationToken cancellationToken)
    {
        var user = _users.Values.FirstOrDefault(x =>
            string.Equals(x.ExternalUserId, externalUserId, StringComparison.Ordinal));

        return Task.FromResult(user);
    }

    public Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var user = _users.Values.FirstOrDefault(x =>
            string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(user);
    }

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        _users.TryGetValue(userId, out var user);

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

    public Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken cancellationToken)
    {
        var role = _roles.Values.FirstOrDefault(x => string.Equals(x.Name, roleName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(role);
    }

    public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken)
    {
        var roles = _roles.Values
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Role>>(roles);
    }

    public Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        _roles.TryGetValue(roleId, out var role);

        return Task.FromResult(role);
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

        foreach (var key in _rolePermissions.Keys.Where(key => key.StartsWith($"{role.Id:N}:", StringComparison.Ordinal)).ToArray())
        {
            _rolePermissions.TryRemove(key, out _);
        }

        foreach (var key in _userRoles.Keys.Where(key => key.EndsWith($":{role.Id:N}", StringComparison.Ordinal)).ToArray())
        {
            _userRoles.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = _permissions.Values
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Permission>>(permissions);
    }

    public Task<IReadOnlyList<Permission>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var permissions = _rolePermissions.Values
            .Where(x => x.RoleId == roleId)
            .Select(x => _permissions[x.PermissionId])
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Permission>>(permissions);
    }

    public Task SetRolePermissionsAsync(
        Guid roleId,
        IReadOnlyCollection<Guid> permissionIds,
        CancellationToken cancellationToken)
    {
        foreach (var key in _rolePermissions.Keys.Where(key => key.StartsWith($"{roleId:N}:", StringComparison.Ordinal)).ToArray())
        {
            _rolePermissions.TryRemove(key, out _);
        }

        foreach (var permissionId in permissionIds.Distinct())
        {
            _rolePermissions[LinkKey(roleId, permissionId)] = new RolePermission(roleId, permissionId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Role>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roles = _userRoles.Values
            .Where(x => x.UserId == userId)
            .Select(x => _roles[x.RoleId])
            .OrderBy(x => x.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<Role>>(roles);
    }

    public Task SetUserRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        foreach (var key in _userRoles.Keys.Where(key => key.StartsWith($"{userId:N}:", StringComparison.Ordinal)).ToArray())
        {
            _userRoles.TryRemove(key, out _);
        }

        foreach (var roleId in roleIds.Distinct())
        {
            _userRoles[LinkKey(userId, roleId)] = new UserRole(userId, roleId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TenantUser>> GetTenantUsersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var users = _tenantUsers.Values
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.CreatedOnUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<TenantUser>>(users);
    }

    public Task<IReadOnlyList<TenantUser>> GetTenantMembershipsByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var users = _tenantUsers.Values
            .Where(x => x.UserId == userId && x.IsEnabled)
            .OrderBy(x => x.CreatedOnUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<TenantUser>>(users);
    }

    public Task<TenantUser?> GetTenantUserAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var tenantUser = _tenantUsers.Values.FirstOrDefault(x => x.TenantId == tenantId && x.UserId == userId);

        return Task.FromResult(tenantUser);
    }

    public Task AddTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken)
    {
        _tenantUsers[tenantUser.Id] = tenantUser;

        return Task.CompletedTask;
    }

    public Task UpdateTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken)
    {
        _tenantUsers[tenantUser.Id] = tenantUser;

        return Task.CompletedTask;
    }

    public Task<bool> HasTenantRoleAsync(
        Guid tenantId,
        string externalUserId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        var user = _users.Values.FirstOrDefault(x =>
            string.Equals(x.ExternalUserId, externalUserId, StringComparison.Ordinal));

        if (user is null)
        {
            return Task.FromResult(false);
        }

        var roleIds = _roles.Values
            .Where(x => roleNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet();

        var hasRole = _tenantUsers.Values.Any(x =>
            x.TenantId == tenantId &&
            x.UserId == user.Id &&
            x.IsEnabled &&
            roleIds.Contains(x.RoleId));

        return Task.FromResult(hasRole);
    }

    private static string LinkKey(Guid leftId, Guid rightId)
    {
        return $"{leftId:N}:{rightId:N}";
    }
}
