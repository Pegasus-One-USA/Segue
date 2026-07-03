using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// EF-backed runtime RBAC bootstrapper. Reads the canonical <see cref="RbacSeedData"/> and inserts any missing
/// built-in PermissionCategory / Permission / Role / role-level PermissionAllocation rows by Id. Existing rows are
/// never modified, so a partially-seeded database (e.g. a new permission added since the last release) is topped up
/// on the next boot without disturbing operator customizations. No users, and no per-user allocations, are ever
/// created here.
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
        var existingCategoryIds = await _dbContext.PermissionCategories
            .IgnoreQueryFilters()
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var categoryIdSet = existingCategoryIds.ToHashSet();

        foreach (var seed in RbacSeedData.Categories)
        {
            if (categoryIdSet.Contains(seed.Id))
            {
                continue;
            }

            _dbContext.PermissionCategories.Add(new PermissionCategory(seed.Id, seed.Name));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var existingPermissionIds = await _dbContext.Permissions
            .IgnoreQueryFilters()
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var permissionIdSet = existingPermissionIds.ToHashSet();

        foreach (var seed in RbacSeedData.Permissions)
        {
            if (permissionIdSet.Contains(seed.Id))
            {
                continue;
            }

            _dbContext.Permissions.Add(
                new Permission(seed.Id, seed.Name, seed.Description, RbacSeedData.CategoryIdsByName[seed.Category], isSystem: true));
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
