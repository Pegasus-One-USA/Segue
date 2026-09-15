using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class Role : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private Role()
    {
    }

    public Role(
        Guid id,
        string name,
        string description,
        bool isSystem = false,
        bool isDefault = false)
    {
        Id = id;
        Name = name;
        Description = description;
        IsSystem = isSystem;
        IsEnabled = true;
        IsDefault = isDefault;
        IsFullAccess = false;
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;
    public string Description { get; private set; } = default!;

    /// <summary>True for the seeded built-in roles; blocks edit/delete of platform roles.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>True for the auto-generated default roles.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>Whether the role is active and grants its permissions.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// RBAC redesign "Full System Access" capability flag: a role with this set is treated as holding
    /// every current and future permission, without enumerating them, AND passes the same
    /// <c>UnifiedAdminAuthorizationHandler</c>/<c>SuperAdminOnlyAuthorizationHandler</c> checks a
    /// SuperAdmin/Admin role-name claim does — both consult this flag directly, never the role's name.
    /// <c>CachedUserPermissionsProvider</c> folds it into the effective-permission-code set (Step 2);
    /// the two role-name authorization handlers OR it in alongside their existing role-name check (Step
    /// 3), so a SuperAdmin/Admin role-name claim keeps authorizing exactly as before — this flag is an
    /// additional path in, not a replacement. Defaults to <see langword="false"/> for every role; only
    /// the built-in SuperAdmin and Admin rows are backfilled to <see langword="true"/> (via migration,
    /// not via this constructor).
    /// </summary>
    public bool IsFullAccess { get; private set; }

    public void Update(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    /// <summary>
    /// Grants or revokes this role's "Full System Access" capability (see <see cref="IsFullAccess"/>).
    /// The gated entry point is <c>RoleManagementService</c> (its internal
    /// <c>ApplyFullAccessChangeIfRequestedAsync</c>, invoked from <c>CreateRoleAsync</c>/
    /// <c>UpdateRoleAsync</c>), which first requires the calling user to themselves hold Full System
    /// Access (<c>CallerHasFullAccessAsync</c>) before calling this setter — a caller can never grant
    /// themselves this capability. Once set, <c>CachedUserPermissionsProvider</c> and the
    /// <c>UnifiedAdminAuthorizationHandler</c>/<c>SuperAdminOnlyAuthorizationHandler</c> authorization
    /// handlers all consult this flag directly.
    /// </summary>
    public void SetFullAccess(bool isFullAccess)
    {
        IsFullAccess = isFullAccess;
    }
}
