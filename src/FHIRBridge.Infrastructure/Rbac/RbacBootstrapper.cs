using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// EF-backed runtime RBAC bootstrapper. Reads the canonical <see cref="RbacSeedData"/> and inserts any missing
/// built-in PermissionCategory / PermissionGroup / Permission / Role / role-level PermissionAllocation rows by Id,
/// so a partially-seeded database (e.g. a new permission added since the last release) is topped up on the next
/// boot without disturbing operator customizations. A permission already seeded under a different Id (e.g. because
/// the Id-derivation formula changed after it was first created) is still recognized by its stable Name and never
/// duplicated — see the by-name fallback below. An existing row's own Id (and everything that references it —
/// Permission.Id, PermissionGroup.Id, PermissionAllocation.PermissionId) is never touched, but <c>DisplayName</c>
/// and the parent-relationship fields (PermissionGroup.CategoryId, Permission.GroupId) are re-synced from the seed
/// data on every boot: re-categorizing a group or re-grouping a permission is just a code change to
/// <see cref="PermissionTaxonomy"/>/<see cref="RbacSeedData"/> that takes effect on next boot, never a migration or
/// a delete-and-recreate, since the row's identity (and every reference to it) stays exactly the same. A built-in
/// permission removed from <see cref="RbacSeedData.Permissions"/> is deactivated (<c>Permission.IsActive = false</c>),
/// never deleted, so historical PermissionAllocations survive; re-declaring it later reactivates the same row.
/// No users, and no per-user allocations, are ever created here.
/// </summary>
public sealed class RbacBootstrapper : IRbacBootstrapper
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ILogger<RbacBootstrapper> _logger;

    public RbacBootstrapper(FHIRBridgeDbContext dbContext, ILogger<RbacBootstrapper> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        // A duplicate value anywhere in the taxonomy (a repeated enum value, Id, or display name, or two
        // seed entries for the same Group+Action) makes every Id/Code this class writes from RbacSeedData
        // suspect -- log it and leave the database exactly as it was rather than risk seeding corrupted data.
        var validationErrors = RbacDefinitionValidator.Validate();
        if (validationErrors.Count > 0)
        {
            foreach (var error in validationErrors)
            {
                _logger.LogError("RBAC definition validation failed: {ValidationError}", error);
            }

            return;
        }

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
            var categoryId = RbacSeedData.CategoryIdsByCode[seed.Group.GetCategory()];

            if (existingGroups.TryGetValue(seed.Id, out var existing))
            {
                // A PermissionGroupCode member can be renamed (its Id, from PermissionGroupAttribute, stays
                // the same) — re-sync Name too, same reason as the Permission-level sync below: it's the
                // stable code identifier other things match on, not just a label.
                if (existing.Name != seed.Name)
                {
                    existing.Update(seed.Name, existing.Description);
                }

                if (existing.DisplayName != seed.DisplayName)
                {
                    existing.UpdateDisplayName(seed.DisplayName);
                }

                if (existing.CategoryId != categoryId)
                {
                    existing.UpdateCategory(categoryId);
                }

                continue;
            }

            _dbContext.PermissionGroups.Add(new PermissionGroup(seed.Id, seed.Name, seed.DisplayName, categoryId));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var existingPermissions = await _dbContext.Permissions
            .IgnoreQueryFilters()
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        // Name ("group.action") is stable even if the Id-derivation formula changes later — falling back to
        // it means an already-seeded permission is always recognized as the same permission and never
        // reinserted under a new Id. Its original Id is never touched; only DisplayName/GroupId/Description
        // are re-synced, same as for an Id-based match. Built by indexer assignment, not ToDictionary: Name
        // is no longer a database-enforced unique key (Id, the primary key, already is the deterministic
        // encoding of Group+Action — see PermissionTaxonomy.BuildPermissionId), so more than one active row
        // sharing a Name would otherwise throw here instead of just picking one.
        var existingPermissionsByName = new Dictionary<string, Permission>(StringComparer.OrdinalIgnoreCase);
        foreach (var permission in existingPermissions.Values)
        {
            if (permission.IsActive)
            {
                existingPermissionsByName[permission.Name] = permission;
            }
        }

        // Maps each seed's theoretically-derived Id (PermissionTaxonomy.BuildPermissionId) to whatever Id the
        // permission actually ends up with in the database — its own Id for a fresh insert, or a pre-existing
        // row's real (possibly differently-derived) Id for a by-Id/by-Name match. RbacSeedData.RolePermissions
        // below references permissions by their theoretical Id, so this translation keeps role grants pointed
        // at the real row even when the two Ids differ.
        var actualPermissionIdBySeedId = new Dictionary<Guid, Guid>();

        // Every built-in permission row still declared by RbacSeedData.Permissions this boot — anything
        // IsSystem that's missing from this set afterward was removed from code and gets deactivated instead
        // of deleted, below.
        var declaredPermissionIds = new HashSet<Guid>();

        foreach (var seed in RbacSeedData.Permissions)
        {
            var groupId = RbacSeedData.GroupIdsByCode[seed.Group];

            var existing = existingPermissions.GetValueOrDefault(seed.Id)
                ?? existingPermissionsByName.GetValueOrDefault(seed.Name);

            if (existing is not null)
            {
                // A Group/Action enum member can be renamed (its int value, and therefore the permission's
                // Id, stays the same) — re-sync Name too, or the stored wire-format code silently goes
                // stale forever: the authorization policy registered at startup is always freshly computed
                // from the current enum name, so a stale stored Name here would stop matching it.
                if (existing.Name != seed.Name)
                {
                    existing.UpdateName(seed.Name);
                }

                if (existing.DisplayName != seed.DisplayName)
                {
                    existing.UpdateDisplayName(seed.DisplayName);
                }

                if (existing.GroupId != groupId)
                {
                    existing.UpdateGroup(groupId);
                }

                // Descriptions can differ when the same permission was independently seeded/discovered from
                // two places (e.g. RbacSeedData vs. a [StandardPermission] attribute). Rather than silently
                // picking one, append the new text so an operator can see both and reconcile — but only once;
                // once merged, seed.Description is already a substring, so this doesn't grow on every boot.
                if (existing.Description != seed.Description
                    && !existing.Description.Contains(seed.Description, StringComparison.OrdinalIgnoreCase))
                {
                    existing.UpdateDescription($"{existing.Description} | {seed.Description}");
                }

                // Re-declared after previously having been removed (see the deactivation pass below) — bring
                // it back rather than leaving it stranded as inactive.
                if (!existing.IsActive)
                {
                    existing.Activate();
                }

                actualPermissionIdBySeedId[seed.Id] = existing.Id;
                declaredPermissionIds.Add(existing.Id);
                continue;
            }

            var created = new Permission(seed.Id, seed.Name, seed.DisplayName, seed.Description, groupId, isSystem: true);
            _dbContext.Permissions.Add(created);
            actualPermissionIdBySeedId[seed.Id] = created.Id;
            declaredPermissionIds.Add(created.Id);
        }

        // A built-in permission no longer declared in code is deactivated, never deleted — existing
        // PermissionAllocations referencing it (role grants, audit history) stay intact, it just can no
        // longer be newly granted. Scoped to IsSystem rows only: permissions discovered at runtime via
        // [StandardPermission] (IsSystem = false) have their own lifecycle in Program.cs and were never
        // declared in RbacSeedData.Permissions to begin with, so they'd always appear "missing" here.
        //
        // GATED (Epic/SQL/CSV-only branch) exception: a permission removed from RbacSeedData.Permissions
        // because its source/destination type was pulled out of SourceSystemPermissionGroups.AllowedGroups
        // (e.g. Athenahealth/Cerner) must also have its existing role grants revoked here, not just the
        // permission row deactivated — this is a deliberate, code-driven "this type no longer exists on this
        // branch" removal, not an operator unchecking a box via the Role Permissions screen (which only ever
        // touches PermissionAllocation rows directly and never this deactivation path), so it doesn't
        // conflict with that screen's "never silently re-grant" guarantee.
        var deactivatedPermissionIds = new List<Guid>();
        foreach (var existing in existingPermissions.Values)
        {
            if (existing.IsSystem && existing.IsActive && !declaredPermissionIds.Contains(existing.Id))
            {
                existing.Deactivate();
                deactivatedPermissionIds.Add(existing.Id);
            }
        }

        if (deactivatedPermissionIds.Count > 0)
        {
            var allocationsToRevoke = await _dbContext.PermissionAllocations
                .Where(a => deactivatedPermissionIds.Contains(a.PermissionId))
                .ToListAsync(cancellationToken);

            if (allocationsToRevoke.Count > 0)
            {
                _dbContext.PermissionAllocations.RemoveRange(allocationsToRevoke);
            }
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

        foreach (var (roleId, seedPermissionIds) in RbacSeedData.RolePermissions)
        {
            foreach (var seedPermissionId in seedPermissionIds)
            {
                var actualPermissionId = actualPermissionIdBySeedId.GetValueOrDefault(seedPermissionId, seedPermissionId);

                if (linkSet.Contains((roleId, actualPermissionId)))
                {
                    continue;
                }

                _dbContext.PermissionAllocations.Add(PermissionAllocation.ForRole(Guid.NewGuid(), roleId, actualPermissionId));
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
