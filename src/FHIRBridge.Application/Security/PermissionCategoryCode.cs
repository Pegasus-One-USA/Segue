namespace FHIRBridge.Application.Security;

/// <summary>
/// Standardized top-level permission categories. A <see cref="StandardPermissionAttribute"/> can only be
/// declared with one of these values. Adding a new category means adding one enum member here with a
/// <see cref="PermissionCategoryAttribute"/> declaring its stable Id and display name — nothing else to
/// touch, since <see cref="RbacSeedData.Categories"/> is generated from this enum directly.
/// </summary>
public enum PermissionCategoryCode
{
    [PermissionCategory("40000000-0000-0000-0000-000000000001", "Access Control")]
    AccessControl = 1,

    [PermissionCategory("40000000-0000-0000-0000-000000000002", "Platform")]
    Platform = 2,

    [PermissionCategory("40000000-0000-0000-0000-000000000003", "Pipelines")]
    Pipelines = 3,
}
