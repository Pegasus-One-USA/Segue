using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Typed replacement for <c>[Authorize(Policy = "HasPermission:code")]</c>. Ties an action/controller to a
/// permission built from standardized enums — never a free-typed string — so <see cref="PermissionCatalog"/>
/// can discover it via reflection and <see cref="PermissionTaxonomy"/> guarantees the group actually belongs
/// to the given category.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class StandardPermissionAttribute : AuthorizeAttribute
{
    public StandardPermissionAttribute(
        PermissionCategoryCode category,
        PermissionGroupCode group,
        PermissionActionCode action,
        string? description = null)
        : base(AuthorizationPolicies.HasPermission(PermissionTaxonomy.BuildPermissionCode(group, action)))
    {
        if (PermissionTaxonomy.GroupCategory[group] != category)
        {
            throw new ArgumentException(
                $"Permission group '{group}' does not belong to category '{category}'.", nameof(category));
        }

        Category = category;
        Group = group;
        Action = action;
        Description = description;
        PermissionCode = PermissionTaxonomy.BuildPermissionCode(group, action);
    }

    public PermissionCategoryCode Category { get; }
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
