namespace FHIRBridge.Domain.Enums;

/// <summary>
/// Where a <see cref="Entities.Permission"/> row's existence is owned/governed from — decides whether the
/// boot-time sync in <c>Program.SyncDiscoveredPermissionsAsync</c> is allowed to deactivate it when it's no
/// longer backed by code. Deliberately NOT a security concept: a permission's <see cref="Source"/> never
/// affects whether it can authorize anything (see <c>PermissionAuthorizationHandler</c>, which resolves
/// effective codes purely from <c>PermissionAllocations</c> and doesn't read this field at all). It only
/// controls catalog/UI lifecycle — whether the row survives being "not currently discovered in code."
/// </summary>
public enum PermissionSource
{
    /// <summary>
    /// Owned by code — either a built-in <c>RbacSeedData.Permissions</c> entry (synced by
    /// <c>RbacBootstrapper</c>) or discovered via <c>[StandardPermission]</c>/<c>[DynamicSourceSystemPermission]</c>
    /// (synced by <c>Program.SyncDiscoveredPermissionsAsync</c>). The default for every permission created by
    /// either pipeline. Eligible for auto-deactivation the moment code no longer declares/discovers it.
    /// </summary>
    Code = 0,

    /// <summary>
    /// Deliberately introduced via a hand-authored EF migration (<c>migrationBuilder.InsertData</c>), ahead of
    /// any endpoint enforcing it — reserving a permission code so it can be granted/displayed before the
    /// feature behind it ships. Never auto-deactivated by <c>SyncDiscoveredPermissionsAsync</c>, since it was
    /// never expected to be code-discovered in the first place. If an endpoint later adds a
    /// <c>[StandardPermission]</c>/<c>[DynamicSourceSystemPermission]</c> for the same code, the row is picked
    /// up by the discovery sync on the next boot and promoted to <see cref="Code"/> (see
    /// <c>SyncDiscoveredPermissionsAsync</c>'s update branch) — from then on it's fully code-governed, same as
    /// anything that started that way.
    /// </summary>
    Migration = 1,
}
