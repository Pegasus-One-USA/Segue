namespace FHIRBridge.Application.Security;

/// <summary>
/// Declares a <see cref="PermissionCategoryCode"/> member's entire identity in one place: its stable seeded
/// Guid and its human-readable display name. Adding a new category means adding one enum member decorated
/// with this attribute — nothing else to touch, since <see cref="RbacSeedData.Categories"/> is generated
/// straight from <c>Enum.GetValues&lt;PermissionCategoryCode&gt;()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class PermissionCategoryAttribute : Attribute
{
    public PermissionCategoryAttribute(string id, string displayName)
    {
        Id = Guid.Parse(id);
        DisplayName = displayName;
    }

    public Guid Id { get; }
    public string DisplayName { get; }
}
