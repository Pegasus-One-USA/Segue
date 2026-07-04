using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// EF-backed runtime RBAC bootstrapper. Reads the canonical <see cref="RbacSeedData"/> and inserts any missing
/// built-in PermissionCategory / PermissionGroup / Permission / Role / role-level PermissionAllocation rows by Id,
/// so a partially-seeded database (e.g. a new permission added since the last release) is topped up on the next
/// boot without disturbing operator customizations. Identity-bearing fields (Name, GroupId, etc.) on existing rows
/// are never touched, but <c>DisplayName</c> is re-synced from the seed data on every boot — it's cosmetic, not a
/// wire-format contract, so a text change in code (e.g. renaming a <see cref="PermissionDisplayNameAttribute"/>)
/// reaches an already-seeded database automatically. No users, and no per-user allocations, are ever created here.
/// </summary>
public sealed class RbacBootstrapper : IRbacBootstrapper
{
    private readonly FHIRBridgeDbContext _dbContext;

    public RbacBootstrapper(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var existingCategories = await _dbContext.PermissionCategories
            .IgnoreQueryFilters()
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        foreach (var seed in RbacSeedData.Categories)
        {
            if (existingCategories.TryGetValue(seed.Id, out var existing))
            {
                if (existing.DisplayName != seed.DisplayName)
                {
                    existing.UpdateDisplayName(seed.DisplayName);
                }

                continue;
            }

            _dbContext.PermissionCategories.Add(new PermissionCategory(seed.Id, seed.Name, seed.DisplayName));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var existingGroups = await _dbContext.PermissionGroups
            .IgnoreQueryFilters()
            .ToDictionaryAsync(g => g.Id, cancellationToken);

        foreach (var seed in RbacSeedData.Groups)
        {
            if (existingGroups.TryGetValue(seed.Id, out var existing))
            {
                if (existing.DisplayName != seed.DisplayName)
                {
                    existing.UpdateDisplayName(seed.DisplayName);
                }

                continue;
            }

            var categoryId = RbacSeedData.CategoryIdsByCode[PermissionTaxonomy.GroupCategory[seed.Group]];
            _dbContext.PermissionGroups.Add(new PermissionGroup(seed.Id, seed.Name, seed.DisplayName, categoryId));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var existingPermissions = await _dbContext.Permissions
            .IgnoreQueryFilters()
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var seed in RbacSeedData.Permissions)
        {
            if (existingPermissions.TryGetValue(seed.Id, out var existing))
            {
                if (existing.DisplayName != seed.DisplayName)
                {
                    existing.UpdateDisplayName(seed.DisplayName);
                }

                continue;
            }

            _dbContext.Permissions.Add(
                new Permission(seed.Id, seed.Name, seed.DisplayName, seed.Description, RbacSeedData.GroupIdsByCode[seed.Group], isSystem: true));
        }

        var existingRoleIds = await _dbContext.Roles
            .IgnoreQueryFilters()
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        var roleIdSet = existingRoleIds.ToHashSet();

        foreach (var seed in RbacSeedData.Roles)
        {
            if (roleIdSet.Contains(seed.Id))
            {
                continue;
            }

            _dbContext.Roles.Add(
                new Role(seed.Id, seed.Name, seed.Description, isSystem: true, isDefault: false));
        }

        var existingLinks = await _dbContext.PermissionAllocations
            .Where(x => x.RoleId != null)
            .Select(x => new { x.RoleId, x.PermissionId })
            .ToListAsync(cancellationToken);
        var linkSet = existingLinks
            .Select(x => (x.RoleId!.Value, x.PermissionId))
            .ToHashSet();

        foreach (var (roleId, permissionIds) in RbacSeedData.RolePermissions)
        {
            foreach (var permissionId in permissionIds)
            {
                if (linkSet.Contains((roleId, permissionId)))
                {
                    continue;
                }

                _dbContext.PermissionAllocations.Add(PermissionAllocation.ForRole(Guid.NewGuid(), roleId, permissionId));
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
