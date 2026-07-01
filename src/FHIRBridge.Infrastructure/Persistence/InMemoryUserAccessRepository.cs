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
            new Role(SeededSecurityIds.GlobalAdminRoleId, UnifiedRoles.GlobalAdmin, "Full platform administrator across all tenants.", isSystem: true),
            new Role(SeededSecurityIds.TenantAdminRoleId, UnifiedRoles.TenantAdmin, "Administers configuration and users within a tenant.", isSystem: true),
            new Role(SeededSecurityIds.PipelineEngineerRoleId, UnifiedRoles.PipelineEngineer, "Builds and runs pipeline configurations within a tenant.", isSystem: true),
            new Role(SeededSecurityIds.AnalystRoleId, UnifiedRoles.Analyst, "Runs pipelines and reviews data and audit output.", isSystem: true),
            new Role(SeededSecurityIds.AuditorRoleId, UnifiedRoles.Auditor, "Read-only access to configuration and audit logs.", isSystem: true)
        }.ToDictionary(role => role.Id));

    private readonly ConcurrentDictionary<Guid, Permission> _permissions = new(
        new[]
        {
            // Original platform permissions.
            new Permission(SeededSecurityIds.TenantsReadPermissionId, UnifiedPermissions.TenantsRead, "Read tenant configuration.", "Tenancy"),
            new Permission(SeededSecurityIds.TenantsWritePermissionId, UnifiedPermissions.TenantsWrite, "Create and update tenants.", "Tenancy"),
            new Permission(SeededSecurityIds.ConfigurationWritePermissionId, UnifiedPermissions.ConfigurationWrite, "Manage source, destination, mapping, webhook, and route configuration.", "Configuration"),
            new Permission(SeededSecurityIds.PipelineExecutePermissionId, UnifiedPermissions.PipelineExecute, "Execute configured pipeline routes.", "Pipeline"),
            new Permission(SeededSecurityIds.AuditLogsReadPermissionId, UnifiedPermissions.AuditLogsRead, "Read operational audit logs.", "Audit"),
            new Permission(SeededSecurityIds.SourceConnectionsTestPermissionId, UnifiedPermissions.SourceConnectionsTest, "Test source system connectivity.", "Configuration"),

            // User-module permissions.
            new Permission(SeededSecurityIds.UserInvitePermissionId, UnifiedPermissions.UserInvite, "Invite a new user to the tenant.", "User"),
            new Permission(SeededSecurityIds.UserViewPermissionId, UnifiedPermissions.UserView, "View the list of users.", "User"),
            new Permission(SeededSecurityIds.UserEditPermissionId, UnifiedPermissions.UserEdit, "Update a user's profile information.", "User"),
            new Permission(SeededSecurityIds.UserDeactivatePermissionId, UnifiedPermissions.UserDeactivate, "Deactivate a user account.", "User"),

            // Role permissions.
            new Permission(SeededSecurityIds.RoleCreatePermissionId, UnifiedPermissions.RoleCreate, "Create a new custom role.", "Role"),
            new Permission(SeededSecurityIds.RoleEditPermissionId, UnifiedPermissions.RoleEdit, "Edit an existing role.", "Role"),
            new Permission(SeededSecurityIds.RoleDeletePermissionId, UnifiedPermissions.RoleDelete, "Delete a custom role.", "Role"),
            new Permission(SeededSecurityIds.RoleAssignPermissionId, UnifiedPermissions.RoleAssign, "Assign or remove roles from users.", "Role"),
            new Permission(SeededSecurityIds.RoleViewPermissionId, UnifiedPermissions.RoleView, "View roles and their permissions.", "Role"),

            // Workflow permissions.
            new Permission(SeededSecurityIds.WorkflowCreatePermissionId, UnifiedPermissions.WorkflowCreate, "Create a new workflow.", "Workflow"),
            new Permission(SeededSecurityIds.WorkflowEditPermissionId, UnifiedPermissions.WorkflowEdit, "Edit an existing workflow.", "Workflow"),
            new Permission(SeededSecurityIds.WorkflowDeletePermissionId, UnifiedPermissions.WorkflowDelete, "Delete a workflow.", "Workflow"),
            new Permission(SeededSecurityIds.WorkflowRunPermissionId, UnifiedPermissions.WorkflowRun, "Execute a workflow.", "Workflow"),
            new Permission(SeededSecurityIds.WorkflowViewPermissionId, UnifiedPermissions.WorkflowView, "View workflow details.", "Workflow"),

            // Tenant permissions.
            new Permission(SeededSecurityIds.TenantSettingsEditPermissionId, UnifiedPermissions.TenantSettingsEdit, "Update organization settings.", "Tenant"),
            new Permission(SeededSecurityIds.TenantBillingViewPermissionId, UnifiedPermissions.TenantBillingView, "View billing and subscription information.", "Tenant"),

            // Report / payload permissions.
            new Permission(SeededSecurityIds.ReportViewPermissionId, UnifiedPermissions.ReportView, "View reports and analytics.", "Report"),
            new Permission(SeededSecurityIds.PayloadViewPermissionId, UnifiedPermissions.PayloadView, "View data payloads from workflow runs.", "Payload")
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

    public Task<IReadOnlyList<User>> GetUsersByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var users = _users.Values
            .Where(x => !x.IsDeleted && x.TenantId == tenantId)
            .OrderBy(x => x.Email)
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

    public Task<IReadOnlyList<Role>> GetRolesByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var roles = _roles.Values
            .Where(x => !x.IsDeleted && x.TenantId == tenantId)
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

        foreach (var key in _rolePermissions.Keys
            .Where(k => k.StartsWith($"{role.Id:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _rolePermissions.TryRemove(key, out _);
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

    public Task<IReadOnlyList<Permission>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var permissions = _rolePermissions.Values
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
        foreach (var key in _rolePermissions.Keys
            .Where(k => k.StartsWith($"{roleId:N}:", StringComparison.Ordinal))
            .ToArray())
        {
            _rolePermissions.TryRemove(key, out _);
        }

        foreach (var permissionId in permissionIds.Distinct())
        {
            _rolePermissions[LinkKey(roleId, permissionId)] = new RolePermission(roleId, permissionId);
        }

        return Task.CompletedTask;
    }

    public Task AddRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        _rolePermissions[LinkKey(roleId, permissionId)] = new RolePermission(roleId, permissionId);

        return Task.CompletedTask;
    }

    public Task RemoveRolePermissionAsync(Guid roleId, Guid permissionId, CancellationToken cancellationToken)
    {
        _rolePermissions.TryRemove(LinkKey(roleId, permissionId), out _);

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

    // ── Tenant Users ─────────────────────────────────────────────────────────

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
        var tenantUser = _tenantUsers.Values
            .FirstOrDefault(x => x.TenantId == tenantId && x.UserId == userId);

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
            !x.IsDeleted &&
            string.Equals(x.ExternalUserId, externalUserId, StringComparison.Ordinal));

        if (user is null)
        {
            return Task.FromResult(false);
        }

        var roleIds = _roles.Values
            .Where(x => !x.IsDeleted && roleNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet();

        var hasRole = _tenantUsers.Values.Any(x =>
            x.TenantId == tenantId &&
            x.UserId == user.Id &&
            x.IsEnabled &&
            roleIds.Contains(x.RoleId));

        return Task.FromResult(hasRole);
    }

    public Task<bool> TenantHasSuperAdminAsync(Guid tenantId, Guid superAdminRoleId, CancellationToken cancellationToken)
    {
        var hasSuperAdmin = _tenantUsers.Values.Any(x =>
            x.TenantId == tenantId &&
            x.IsEnabled &&
            x.RoleId == superAdminRoleId);

        return Task.FromResult(hasSuperAdmin);
    }

    private static string LinkKey(Guid leftId, Guid rightId)
    {
        return $"{leftId:N}:{rightId:N}";
    }
}
