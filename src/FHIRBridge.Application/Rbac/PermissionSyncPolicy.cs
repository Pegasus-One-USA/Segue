using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Security;

/// <summary>
/// Pure decision logic for whether <c>Program.SyncDiscoveredPermissionsAsync</c>'s orphan-deactivation pass
/// should touch a given <see cref="Domain.Entities.Permission"/> row this boot. Pulled out of that method
/// (a local function inside top-level-statement <c>Program.cs</c>, which can't itself be unit tested) so the
/// actual rule — the one piece of this whole area with real correctness risk — has direct test coverage
/// independent of a full app boot.
/// </summary>
public static class PermissionSyncPolicy
{
    /// <summary>
    /// True when an existing, active permission row should be deactivated because code no longer accounts
    /// for it. A row is left alone (returns <see langword="false"/>) if any of:
    /// <list type="bullet">
    /// <item>it's already inactive — nothing to do;</item>
    /// <item>it's declared in <c>RbacSeedData.Permissions</c> — owned by <c>RbacBootstrapper</c>, never this method;</item>
    /// <item>it's still discovered via <c>[StandardPermission]</c>/<c>[DynamicSourceSystemPermission]</c> this boot;</item>
    /// <item><b>it's <see cref="PermissionSource.Migration"/></b> — deliberately introduced via a hand-authored
    /// migration ahead of any code enforcing it (see <see cref="PermissionSource"/>'s own doc comment); it was
    /// never expected to be code-discovered, so "not currently discovered" carries no information about
    /// whether it's still wanted.</item>
    /// </list>
    /// </summary>
    public static bool ShouldDeactivateOrphan(
        bool isActive,
        PermissionSource source,
        bool isSeedDeclared,
        bool isDiscoveredThisBoot)
    {
        return isActive
            && source == PermissionSource.Code
            && !isSeedDeclared
            && !isDiscoveredThisBoot;
    }
}
