using System.Reflection;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Api.Rbac;

/// <summary>
/// Reflection discovers every [StandardPermission] code in use (see PermissionCatalog), but only
/// registering an in-memory authorization policy for it isn't enough to let anyone through — the code
/// also has to exist as a Permission row before any role can be granted it. This closes that gap
/// automatically at startup instead of requiring a manual PermissionConfiguration + migration edit for
/// every new permission-gated feature.
///
/// Pulled out of Program.cs (where it was a private local function) purely so it's directly unit-testable
/// — tests/FHIRBridge.UnitTests already references this project — without reflection or a
/// WebApplicationFactory host. Logic is otherwise unchanged from what previously lived there; the one
/// behavior change made in the same commit is documented on <see cref="SyncAsync"/>'s grant-loop filter
/// (RBAC redesign Step 5).
/// </summary>
public static class DiscoveredPermissionSynchronizer
{
    /// <param name="repository">Real repository at runtime; a mock/fake in tests.</param>
    /// <param name="logger">Startup logger.</param>
    /// <param name="scanAssembly">
    /// Assembly to scan for [StandardPermission]/[DynamicSourceSystemPermission] attributes. Defaults to
    /// this assembly (FHIRBridge.Api — where every real controller lives), exactly matching the one
    /// production call site in Program.cs, which never passes this parameter. Overridable purely so a test
    /// can point discovery at a small, fully-controlled test assembly instead of the real, ~150-permission
    /// production catalog — never used any other way.
    /// </param>
    public static async Task SyncAsync(IUserAccessRepository repository, ILogger logger, Assembly? scanAssembly = null)
    {
        // Every permission referenced by a [StandardPermission] attribute. PermissionCatalog already
        // deduplicates these by code and combines descriptions/instances, so each entry here is unique
        // by Id — no further grouping needed.
        var discoveredPermissions = PermissionCatalog.DiscoveredPermissions(scanAssembly ?? typeof(DiscoveredPermissionSynchronizer).Assembly);
        var discoveredPermissionsById = discoveredPermissions.ToDictionary(p => p.Id);

        var existingPermissions = await repository.GetPermissionsAsync(CancellationToken.None);
        var existingPermissionsById = existingPermissions.ToDictionary(p => p.Id);

        // Permissions declared in RbacSeedData are owned by RbacBootstrapper and must never be
        // deactivated by this method.
        var seedDeclaredPermissionIds = new HashSet<Guid>(RbacSeedData.Permissions.Select(p => p.Id));

        // Every role that exists at the moment a brand-new dynamically-discovered permission is first
        // created — see the grant loop in step 2/3 below for exactly which of these get an explicit
        // allocation and why.
        var allRoles = await repository.GetRolesAsync(CancellationToken.None);

        // 1. Deactivate a non-seeded permission that's active but no longer discovered in code.
        foreach (var existingPermission in existingPermissions)
        {
            if (!existingPermission.IsActive)
            {
                continue;
            }

            if (seedDeclaredPermissionIds.Contains(existingPermission.Id))
            {
                continue;
            }

            if (!discoveredPermissionsById.ContainsKey(existingPermission.Id))
            {
                existingPermission.Deactivate();
                await repository.UpdatePermissionAsync(existingPermission, CancellationToken.None);
            }
        }

        // 2 & 3. Update an existing permission's fields, or create a new one.
        foreach (var discoveredPermission in discoveredPermissions)
        {
            var displayName = PermissionTaxonomy.BuildPermissionDisplayName(
                discoveredPermission.Group,
                discoveredPermission.Action);

            var description = string.IsNullOrWhiteSpace(discoveredPermission.Description)
                ? $"Auto-registered permission for '{discoveredPermission.Code}'."
                : discoveredPermission.Description;

            if (existingPermissionsById.TryGetValue(discoveredPermission.Id, out var existingPermission))
            {
                // Keep every mutable field synchronized with what's currently discovered from source code.
                var changed = false;

                if (existingPermission.Name != discoveredPermission.Code)
                {
                    existingPermission.UpdateName(discoveredPermission.Code);
                    changed = true;
                }

                if (existingPermission.DisplayName != displayName)
                {
                    existingPermission.UpdateDisplayName(displayName);
                    changed = true;
                }

                if (!string.Equals(existingPermission.Description, description, StringComparison.Ordinal))
                {
                    existingPermission.UpdateDescription(description);
                    changed = true;
                }

                if (existingPermission.Instances != discoveredPermission.Instances)
                {
                    existingPermission.UpdateInstances(discoveredPermission.Instances);
                    changed = true;
                }

                if (!existingPermission.IsActive)
                {
                    existingPermission.Activate();
                    changed = true;
                }

                if (changed)
                {
                    await repository.UpdatePermissionAsync(existingPermission, CancellationToken.None);
                }

                continue;
            }

            // New permission.
            var groupId = RbacSeedData.GroupIdsByCode[discoveredPermission.Group];

            var newPermission = new Permission(
                discoveredPermission.Id,
                discoveredPermission.Code,
                displayName,
                description,
                groupId,
                isSystem: false,
                instances: discoveredPermission.Instances);

            await repository.AddPermissionAsync(newPermission, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(discoveredPermission.Description))
            {
                logger.LogWarning(
                    "Auto-registered new permission '{PermissionCode}' discovered via [StandardPermission] with no description; add one to the attribute.",
                    discoveredPermission.Code);
            }

            // RBAC redesign Step 5 (previously: grant to every role that exists, unconditionally — flagged
            // by the Phase 1 investigation as a bug relative to the target model, since that silently gave
            // every custom role a permission nobody explicitly assigned it). A role with IsFullAccess =
            // true (see Role.IsFullAccess) already automatically holds every currently-active permission
            // through CachedUserPermissionsProvider's live union (Step 2) — it needs no PermissionAllocation
            // row here at all, now or ever, for a brand-new permission to work for it. So this loop now
            // only grants an explicit allocation to a role that is IsSystem but NOT already IsFullAccess
            // (today: Operations, Audit) — preserving exactly the historical "nothing loses access to a
            // source/destination it could already use before this permission existed" guarantee for those
            // two roles specifically, the same way it always has. Every other role — a normal custom role,
            // or a custom role marked Full System Access — is deliberately excluded: a normal custom role
            // must never auto-receive a permission it was never explicitly given (this was the actual bug),
            // and a full-access role never needs an explicit row to begin with. This only ever runs inside
            // the "new permission" branch above (the `continue` a few lines up handles the "already exists"
            // case), and a Permission row is only ever created once in its lifetime (removed permissions
            // are deactivated, never deleted — see step 1 above) — so this grant loop can only ever fire
            // once per permission, and AddRolePermissionAsync is itself idempotent besides. A role that has
            // this permission unchecked later via the Role Permissions screen is therefore never silently
            // re-granted it on a subsequent restart.
            foreach (var role in allRoles.Where(role => role.IsSystem && !role.IsFullAccess))
            {
                await repository.AddRolePermissionAsync(role.Id, newPermission.Id, CancellationToken.None);
            }
        }
    }
}
