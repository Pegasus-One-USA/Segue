namespace FHIRBridge.Application.Security;

/// <summary>
/// Standardized top-level permission categories. A <see cref="StandardPermissionAttribute"/> can only be
/// declared with one of these values — adding a new category means adding an enum member here, never a
/// free-typed string.
/// </summary>
public enum PermissionCategoryCode
{
    [PermissionDisplayName("Access Control")]
    AccessControl = 1,

    [PermissionDisplayName("Platform")]
    Platform = 2,
}
