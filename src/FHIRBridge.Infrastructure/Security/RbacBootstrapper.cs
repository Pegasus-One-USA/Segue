using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// EF-backed runtime RBAC bootstrapper. Reads the canonical <see cref="RbacSeedData"/> and inserts any missing
/// built-in Permission / Role / RolePermission rows by Id. Existing rows are never modified, so a partially-seeded
/// database (e.g. a new permission added since the last release) is topped up on the next boot without disturbing
/// operator customizations. No users are created here.
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
                new Permission(seed.Id, seed.Name, seed.Description, seed.Category, isSystem: true));
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

        var existingLinks = await _dbContext.RolePermissions
            .Select(rp => new { rp.RoleId, rp.PermissionId })
            .ToListAsync(cancellationToken);
        var linkSet = existingLinks
            .Select(x => (x.RoleId, x.PermissionId))
            .ToHashSet();

        foreach (var (roleId, permissionIds) in RbacSeedData.RolePermissions)
        {
            foreach (var permissionId in permissionIds)
            {
                if (linkSet.Contains((roleId, permissionId)))
                {
                    continue;
                }

                _dbContext.RolePermissions.Add(new RolePermission(roleId, permissionId));
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
