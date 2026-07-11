using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Typed replacement for <c>[Authorize(Policy = "HasPermission:code")]</c>. Ties an action/controller to a
/// permission built from standardized enums — never a free-typed string — so <see cref="PermissionCatalog"/>
/// can discover it via reflection. A group belongs to exactly one category (declared on the group's own
/// <see cref="PermissionGroupAttribute"/>), so category is never a separate argument here — that would just
/// be a second way to say something the group's own attribute already decides, and the two could disagree.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class StandardPermissionAttribute : AuthorizeAttribute
{
    public StandardPermissionAttribute(
        PermissionGroupCode group,
        PermissionActionCode action,
        string? description = null)
        : base(AuthorizationPolicies.HasPermission(PermissionTaxonomy.BuildPermissionCode(group, action)))
    {
        Group = group;
        Action = action;
        Description = description;
        PermissionCode = PermissionTaxonomy.BuildPermissionCode(group, action);
    }

    public PermissionGroupCode Group { get; }
    public PermissionActionCode Action { get; }
    public string PermissionCode { get; }

    /// <summary>
    /// Optional human-readable explanation of what this permission grants, shown in permission-management UI.
    /// When a permission is first discovered via this attribute (see <see cref="PermissionCatalog"/>) and has
    /// no seeded description yet, this is used as its <c>Permission.Description</c>.
    /// </summary>
    public string? Description { get; }
}
