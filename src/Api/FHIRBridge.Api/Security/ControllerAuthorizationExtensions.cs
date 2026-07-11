using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Resource-based permission checks for endpoints where the required permission can only be known
/// once request data is inspected — e.g. a source connection's permission group depends on which
/// vendor it's for. <see cref="StandardPermissionAttribute"/> can't express this: it's evaluated at
/// startup with no access to the request. Call this from inside the action body instead.
/// </summary>
public static class ControllerAuthorizationExtensions
{
    /// <summary>
    /// Checks whether the current user has the permission built from <paramref name="group"/> and
    /// <paramref name="action"/>. Returns <c>null</c> when authorized; otherwise the 403 result the
    /// caller should return immediately (<c>if (denied is not null) return denied;</c>).
    /// </summary>
    public static async Task<IActionResult?> AuthorizePermissionAsync(
        this ControllerBase controller,
        IAuthorizationService authorizationService,
        PermissionGroupCode group,
        PermissionActionCode action)
    {
        var code = PermissionTaxonomy.BuildPermissionCode(group, action);
        var result = await authorizationService.AuthorizeAsync(
            controller.User,
            AuthorizationPolicies.HasPermission(code));

        return result.Succeeded ? null : controller.Forbid();
    }

    /// <summary>
    /// Same check as the <see cref="PermissionGroupCode"/> overload, but resolves the group from a
    /// vendor/source-type enum value (e.g. a request's <c>SourceSystemType</c>) via
    /// <see cref="SourceSystemPermissionGroups.GroupFor"/> first. This is the one call any controller
    /// needs for a <see cref="DynamicSourceSystemPermissionAttribute"/>-marked action — it resolves the
    /// exact same way <see cref="PermissionCatalog"/> resolved the permission at startup, so the two
    /// can never disagree about which group a given enum value belongs to.
    /// </summary>
    public static Task<IActionResult?> AuthorizePermissionAsync(
        this ControllerBase controller,
        IAuthorizationService authorizationService,
        Enum sourceSystemValue,
        PermissionActionCode action)
    {
        var group = SourceSystemPermissionGroups.GroupFor(sourceSystemValue);
        return controller.AuthorizePermissionAsync(authorizationService, group, action);
    }
}
