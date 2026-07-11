namespace FHIRBridge.Application.Security;

/// <summary>
/// Declares a <see cref="PermissionGroupCode"/> member's entire identity in one place: its stable seeded Guid,
/// its owning <see cref="PermissionCategoryCode"/>, and its human-readable display name. Adding a new group
/// means adding one enum member decorated with this attribute — nothing else to touch, since
/// <see cref="RbacSeedData.Groups"/> is generated straight from <c>Enum.GetValues&lt;PermissionGroupCode&gt;()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class PermissionGroupAttribute : Attribute
{
    public PermissionGroupAttribute(string id, PermissionCategoryCode category, string displayName)
    {
        Id = Guid.Parse(id);
        Category = category;
        DisplayName = displayName;
    }

    public Guid Id { get; }
    public PermissionCategoryCode Category { get; }
    public string DisplayName { get; }
}
