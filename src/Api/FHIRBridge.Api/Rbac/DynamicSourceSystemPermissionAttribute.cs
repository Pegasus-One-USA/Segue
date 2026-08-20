using FHIRBridge.Application.Security;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Declares that an action performs a resource-based permission check — via
/// <see cref="ControllerAuthorizationExtensions.AuthorizePermissionAsync"/> and
/// <see cref="SourceSystemPermissionGroups"/> — instead of a single fixed
/// <see cref="StandardPermissionAttribute"/>, because the actual group is only known once the
/// request's enum value (e.g. <c>SourceSystemType</c>) is inspected. This attribute does not enforce
/// anything by itself (unlike <see cref="StandardPermissionAttribute"/>, which extends
/// <c>AuthorizeAttribute</c>); it only tells <see cref="PermissionCatalog"/> which enum type and
/// action to cross — looping every value of <paramref name="enumType"/> through
/// <see cref="SourceSystemPermissionGroups.GroupFor"/> — so one permission per resolved group is
/// auto-discovered. The actual authorization call still has to be made explicitly in the action body,
/// passing the same real enum value the request resolved to <see cref="ControllerAuthorizationExtensions.AuthorizePermissionAsync"/>.
/// </summary>
// AllowMultiple: true — an endpoint whose required action depends on the request body (e.g.
// /workflows/build treats a spec with no ExistingId as a create, one with an ExistingId as an
// edit) has to declare every action it can actually require so PermissionCatalog discovers all
// of them, even though only one is checked per real request.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class DynamicSourceSystemPermissionAttribute : Attribute
{
    public DynamicSourceSystemPermissionAttribute(Type enumType, PermissionActionCode action, string? description = null)
    {
        if (!enumType.IsEnum)
        {
            throw new ArgumentException($"'{enumType.Name}' is not an enum type.", nameof(enumType));
        }

        EnumType = enumType;
        Action = action;
        Description = description;
    }

    /// <summary>The enum type whose values are crossed with <see cref="Action"/> to discover permissions.</summary>
    public Type EnumType { get; }

    public PermissionActionCode Action { get; }

    public string? Description { get; }
}
