using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed class RoleManagementService : IRoleManagementService
{
    private static readonly Guid[] SystemRoleIds =
    [
        SeededSecurityIds.SuperAdminRoleId,
        SeededSecurityIds.AdminRoleId,
        SeededSecurityIds.OperationsRoleId,
        SeededSecurityIds.AuditRoleId
    ];

    private readonly IUserAccessRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public RoleManagementService(
        IUserAccessRepository repository,
        ICurrentUserService currentUserService,
        IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _currentUserService = currentUserService;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await _repository.GetRolesAsync(cancellationToken);
        var dtos = new List<RoleDto>();
        foreach (var role in roles)
        {
            dtos.Add(await ToDtoAsync(role, cancellationToken));
        }

        return dtos;
    }

    public async Task<PagedResult<RoleDto>> GetPagedRolesAsync(
        string? search, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken)
    {
        var dtos = await GetRolesAsync(cancellationToken);

        IEnumerable<RoleDto> query = string.IsNullOrWhiteSpace(search)
            ? dtos
            : dtos.Where(r =>
                r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.Description.Contains(search, StringComparison.OrdinalIgnoreCase));

        query = sortDescending switch
        {
            true => query.OrderByDescending(r => r.ModifiedOnUtc ?? r.CreatedOnUtc),
            false => query.OrderBy(r => r.ModifiedOnUtc ?? r.CreatedOnUtc),
            null => query.OrderBy(r => r.Name),
        };

        var all = query.ToArray();
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return new PagedResult<RoleDto>(items, all.Length, page, pageSize);
    }

    public async Task<RoleDto> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        return await ToDtoAsync(role, cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = await _repository.GetPermissionsAsync(cancellationToken);

        return permissions.Select(ToDto).ToArray();
    }

    public async Task<IReadOnlyList<PermissionCatalogCategoryDto>> GetPermissionCatalogAsync(CancellationToken cancellationToken)
    {
        var categories = await _repository.GetPermissionCategoriesAsync(cancellationToken);
        var groups = await _repository.GetPermissionGroupsAsync(cancellationToken);
        var permissions = await _repository.GetPermissionsAsync(cancellationToken);

        var permissionsByGroupId = permissions
            .Where(p => p.IsVisible && p.IsActive && p.GroupId.HasValue)
            .GroupBy(p => p.GroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(ToDto).ToArray());

        var groupsByCategoryId = groups
            .Where(g => g.IsVisible && permissionsByGroupId.ContainsKey(g.Id))
            .GroupBy(g => g.CategoryId)
            .ToDictionary(g => g.Key, g => g
                .Select(group => new PermissionCatalogGroupDto(
                    group.Id, group.Name, group.DisplayName, permissionsByGroupId[group.Id]))
                .ToArray());

        return categories
            .Where(c => c.IsVisible && groupsByCategoryId.ContainsKey(c.Id))
            .Select(c => new PermissionCatalogCategoryDto(c.Id, c.Name, c.DisplayName, groupsByCategoryId[c.Id]))
            .ToArray();
    }

    public async Task<IReadOnlyList<PermissionDto>> GetRolePermissionsAsync(
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var permissions = await _repository.GetRolePermissionsAsync(roleId, cancellationToken);

        return permissions.Where(p => p.IsActive).Select(ToDto).ToArray();
    }

    public async Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        Validate(request.Name, request.Description);
        if (await _repository.GetRoleByNameAsync(request.Name.Trim(), cancellationToken) is not null)
        {
            throw new InvalidOperationException("A role with this name already exists.");
        }

        await ValidatePermissionsAsync(request.PermissionIds, cancellationToken);

        // RBAC redesign Step 4: "Full System Access" is itself a protected capability — a caller cannot
        // create a role with IsFullAccess = true unless they already hold Full System Access themselves
        // (via one of THEIR OWN assigned roles). This is deliberately independent of role.create: holding
        // role.create lets you make roles, not necessarily ones as powerful as SuperAdmin/Admin. A request
        // that doesn't set this field (the default, false) is completely unaffected — every existing
        // caller keeps creating roles exactly as before.
        if (request.IsFullAccess && !await CallerHasFullAccessAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Only a caller with Full System Access can create a role with Full System Access.");
        }

        var role = new Role(Guid.NewGuid(), request.Name.Trim(), request.Description.Trim());
        if (request.IsFullAccess)
        {
            role.SetFullAccess(true);
        }

        await _repository.AddRoleAsync(role, cancellationToken);
        await _repository.SetRolePermissionsAsync(role.Id, request.PermissionIds, cancellationToken);

        return await ToDtoAsync(role, cancellationToken);
    }

    public async Task<RoleDto> UpdateRoleAsync(
        Guid roleId,
        UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request.Name, request.Description);
        await ValidatePermissionsAsync(request.PermissionIds, cancellationToken);

        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        if (role.IsSystem)
        {
            // SuperAdmin's grant is always "every permission that exists" (see
            // SystemRoleDefaultPermissions) — immutable in both name/description and permissions.
            if (role.Id == SeededSecurityIds.SuperAdminRoleId)
            {
                throw new InvalidOperationException("System roles cannot be modified.");
            }

            // The other built-in roles (Admin, Operations, Audit) keep their name/description fixed —
            // same as SuperAdmin — but a SuperAdmin CAN edit their permission grants via the Role
            // Permissions screen, which always resends the role's own name/description unchanged (see
            // role-permissions.component.ts's save()). A real rename/re-describe attempt on one of
            // these still isn't allowed here; that stays the role dialog's own separate concern.
            if (!string.Equals(role.Name, request.Name.Trim(), StringComparison.Ordinal) ||
                !string.Equals(role.Description, request.Description.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("System roles' name and description cannot be modified.");
            }

            // RBAC redesign Step 4 — see CreateRoleAsync's comment for the general rule. Applies here too
            // (e.g. revoking Admin's Full System Access), just never for SuperAdmin, which already threw
            // above and never reaches this line.
            var superAdminFullAccessChanged = await ApplyFullAccessChangeIfRequestedAsync(role, request.IsFullAccess, cancellationToken);
            if (superAdminFullAccessChanged)
            {
                await _repository.UpdateRoleAsync(role, cancellationToken);
            }

            await _repository.SetRolePermissionsAsync(role.Id, request.PermissionIds, cancellationToken);
            return await ToDtoAsync(role, cancellationToken);
        }

        var duplicate = await _repository.GetRoleByNameAsync(request.Name.Trim(), cancellationToken);
        if (duplicate is not null && duplicate.Id != role.Id)
        {
            throw new InvalidOperationException("A role with this name already exists.");
        }

        role.Update(request.Name.Trim(), request.Description.Trim());

        // RBAC redesign Step 4: "Full System Access" is itself a protected capability — a caller cannot
        // grant OR revoke it on any role unless they already hold Full System Access themselves (via one
        // of THEIR OWN assigned roles), independent of whatever role.edit permission got them into this
        // method at all. request.IsFullAccess is null (or already matches the role's current value) for
        // the overwhelming majority of calls — a genuine no-op, not gated at all — see
        // UpdateRoleRequest.IsFullAccess's own comment for exactly why that matters for backward
        // compatibility.
        await ApplyFullAccessChangeIfRequestedAsync(role, request.IsFullAccess, cancellationToken);

        await _repository.UpdateRoleAsync(role, cancellationToken);
        await _repository.SetRolePermissionsAsync(role.Id, request.PermissionIds, cancellationToken);

        return await ToDtoAsync(role, cancellationToken);
    }

    /// <summary>
    /// RBAC redesign Step 4. Applies a genuine, explicit change to <see cref="Role.IsFullAccess"/> — a
    /// null request, or one that already matches the role's current value, is a no-op and never consults
    /// the caller's own access at all (see <see cref="UpdateRoleRequest.IsFullAccess"/>'s doc comment).
    /// Only an actual grant or revoke is gated: it requires the ACTING caller to already hold Full System
    /// Access via one of their own assigned roles — checked fresh from the repository, never trusted from
    /// a claim, so a caller can never grant themselves this capability through this field. Returns true
    /// when the in-memory <paramref name="role"/> was actually mutated, so a caller that doesn't
    /// unconditionally persist the role afterward (the IsSystem branch above) knows a write is needed.
    /// </summary>
    private async Task<bool> ApplyFullAccessChangeIfRequestedAsync(
        Role role, bool? requestedIsFullAccess, CancellationToken cancellationToken)
    {
        if (requestedIsFullAccess is not { } wantsFullAccess || wantsFullAccess == role.IsFullAccess)
        {
            return false;
        }

        if (!await CallerHasFullAccessAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Only a caller with Full System Access can grant or revoke Full System Access.");
        }

        role.SetFullAccess(wantsFullAccess);
        return true;
    }

    /// <summary>Whether the current caller holds Full System Access via any of their OWN assigned roles —
    /// resolved fresh from the repository (never from a JWT role-name claim), mirroring exactly how
    /// UnifiedAdminAuthorizationHandler/SuperAdminOnlyAuthorizationHandler resolve the same flag for
    /// per-request authorization (RBAC redesign Step 3). A caller with no resolvable user id (e.g. a
    /// non-interactive/system context) never has it.</summary>
    private async Task<bool> CallerHasFullAccessAsync(CancellationToken cancellationToken)
    {
        var callerId = _currentUserService.CurrentUser.UserId;
        if (callerId is null)
        {
            return false;
        }

        var callerRoles = await _repository.GetUserRolesAsync(callerId.Value, cancellationToken);
        return callerRoles.Any(r => r.IsFullAccess);
    }

    public async Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        // SuperAdmin alone is truly non-deletable — the platform must always have at least one role that
        // can never be locked out of its own permissions. Every other role, built-in (Admin/Operations/Audit,
        // where they still exist — see RbacSeedData.Roles) or custom, is deletable once no user holds it.
        if (roleId == SeededSecurityIds.SuperAdminRoleId)
        {
            throw new InvalidOperationException("The SuperAdmin role cannot be deleted.");
        }

        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        var userCount = await _repository.GetRoleUserCountAsync(roleId, cancellationToken);
        if (userCount > 0)
        {
            throw new InvalidOperationException(
                $"This role is assigned to {userCount} user(s) and cannot be deleted. Remove the role from all users first.");
        }

        await _repository.DeleteRoleAsync(role, cancellationToken);
    }

    public async Task<RoleDto> AddRolePermissionsAsync(
        Guid roleId,
        AddRolePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        await ValidatePermissionsAsync(request.PermissionIds, cancellationToken);

        foreach (var permissionId in request.PermissionIds.Distinct())
        {
            await _repository.AddRolePermissionAsync(roleId, permissionId, cancellationToken);
        }

        return await ToDtoAsync(role, cancellationToken);
    }

    public async Task RemoveRolePermissionAsync(
        Guid roleId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        _ = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        // Same rule as UpdateRoleAsync's permission-edit path (the one the Role Permissions screen actually
        // uses): only SuperAdmin's grant is immutable.
        if (roleId == SeededSecurityIds.SuperAdminRoleId)
        {
            throw new InvalidOperationException("Permissions cannot be removed from the SuperAdmin role.");
        }

        await _repository.RemoveRolePermissionAsync(roleId, permissionId, cancellationToken);
    }

    private async Task<RoleDto> ToDtoAsync(Role role, CancellationToken cancellationToken)
    {
        var permissions = await _repository.GetRolePermissionsAsync(role.Id, cancellationToken);
        var createdBy = await _userDisplayNameResolver.ResolveOneAsync(role.CreatedBy, cancellationToken);
        var modifiedBy = await _userDisplayNameResolver.ResolveOneAsync(role.ModifiedBy, cancellationToken);

        return new RoleDto(
            role.Id,
            role.Name,
            role.Description,
            permissions.Where(p => p.IsActive).Select(ToDto).ToArray(),
            SystemRoleIds.Contains(role.Id) || role.IsSystem,
            role.CreatedOnUtc,
            createdBy,
            role.ModifiedOnUtc,
            modifiedBy,
            role.IsFullAccess);
    }

    private static PermissionDto ToDto(Permission permission)
    {
        return new PermissionDto(permission.Id, permission.Name, permission.DisplayName, permission.Description, permission.GroupId, permission.IsVisible);
    }

    private async Task ValidatePermissionsAsync(
        IReadOnlyCollection<Guid> permissionIds,
        CancellationToken cancellationToken)
    {
        var validPermissionIds = (await _repository.GetPermissionsAsync(cancellationToken))
            .Select(x => x.Id)
            .ToHashSet();

        if (permissionIds.Any(permissionId => !validPermissionIds.Contains(permissionId)))
        {
            throw new InvalidOperationException("One or more permissions are invalid.");
        }
    }

    private static void Validate(string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Role name is required.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("Role description is required.");
        }
    }
}
