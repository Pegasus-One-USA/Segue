using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Rbac.NodeCatalog;
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

    // The single canonical Node Catalog — built over the exact same data GetPermissionCatalogAsync
    // already returns (so the two can never disagree about which permissions a node has), plus the
    // NodeCatalogMetadata registry for display/rollout metadata. See NodeCatalogBuilder's own doc
    // comment for why this reuse is safe and deliberate.
    public async Task<IReadOnlyList<NodeCatalogEntryDto>> GetNodeCatalogAsync(CancellationToken cancellationToken)
    {
        var permissionCatalog = await GetPermissionCatalogAsync(cancellationToken);
        return NodeCatalogBuilder.Build(permissionCatalog);
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

        var role = new Role(Guid.NewGuid(), request.Name.Trim(), request.Description.Trim());
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
            throw new InvalidOperationException("System roles cannot be modified.");
        }

        var duplicate = await _repository.GetRoleByNameAsync(request.Name.Trim(), cancellationToken);
        if (duplicate is not null && duplicate.Id != role.Id)
        {
            throw new InvalidOperationException("A role with this name already exists.");
        }

        role.Update(request.Name.Trim(), request.Description.Trim());
        await _repository.UpdateRoleAsync(role, cancellationToken);
        await _repository.SetRolePermissionsAsync(role.Id, request.PermissionIds, cancellationToken);

        return await ToDtoAsync(role, cancellationToken);
    }

    public async Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        if (SystemRoleIds.Contains(roleId))
        {
            throw new InvalidOperationException("System roles cannot be deleted.");
        }

        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        if (role.IsSystem)
        {
            throw new InvalidOperationException("System roles cannot be deleted.");
        }

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
        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        if (role.IsSystem && SystemRoleIds.Contains(roleId))
        {
            throw new InvalidOperationException("Permissions cannot be removed from system roles.");
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
            modifiedBy);
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
