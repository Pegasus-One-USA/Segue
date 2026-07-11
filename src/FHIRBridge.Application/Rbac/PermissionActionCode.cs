namespace FHIRBridge.Application.Security;

/// <summary>
/// Standardized permission actions. Combined with a <see cref="PermissionGroupCode"/> via
/// <see cref="PermissionTaxonomy.BuildPermissionCode"/> to form the wire-format permission code
/// (e.g. <c>"user.view"</c>), and via <see cref="PermissionTaxonomy.BuildPermissionDisplayName"/> to form
/// the human-readable display name (e.g. <c>"View User"</c>).
/// </summary>
public enum PermissionActionCode
{
    [PermissionDisplayName("View")]
    View = 1,

    [PermissionDisplayName("Create")]
    Create = 2,

    [PermissionDisplayName("Edit")]
    Edit = 3,

    [PermissionDisplayName("Delete")]
    Delete = 4,

    [PermissionDisplayName("Invite")]
    Invite = 5,

    [PermissionDisplayName("Deactivate")]
    Deactivate = 6,

    [PermissionDisplayName("Assign")]
    Assign = 7,

    [PermissionDisplayName("Run")]
    Run = 8,

    [PermissionDisplayName("Execute")]
    Execute = 9,

    [PermissionDisplayName("Write")]
    Write = 10,

    [PermissionDisplayName("Read")]
    Read = 11,

    [PermissionDisplayName("Test")]
    Test = 12,
}
