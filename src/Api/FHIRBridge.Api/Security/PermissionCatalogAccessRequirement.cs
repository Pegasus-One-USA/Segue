using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>Marker requirement for <see cref="AuthorizationPolicies.PermissionCatalogAccess"/> — the actual
/// UnifiedAdmin-role-OR-permission check lives in <see cref="PermissionCatalogAccessAuthorizationHandler"/>.</summary>
public sealed class PermissionCatalogAccessRequirement : IAuthorizationRequirement
{
    /// <summary>Anyone who can view a role's permissions (the Role Permissions screen, reachable with
    /// role.view alone per user-management.routes.ts) needs the full permission catalog to render its
    /// read-only grid — there is no way to show "which permissions exist and which are granted" without it.</summary>
    public static readonly string RoleViewCode =
        PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Role, PermissionActionCode.View);
}
