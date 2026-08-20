using System.Security.Claims;
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
    /// The actual permission-check core, independent of <see cref="ControllerBase"/> — usable from a plain
    /// <see cref="ClaimsPrincipal"/>/<see cref="IAuthorizationService"/> pair, e.g. from a Minimal API endpoint
    /// (see <c>WorkflowEndpoints</c>) that has no controller instance to hang <c>Forbid()</c> off of. The
    /// <see cref="ControllerBase"/>-bound overloads below both delegate to this so the two call sites can never
    /// resolve a permission differently.
    /// </summary>
    public static async Task<bool> HasPermissionAsync(
        IAuthorizationService authorizationService,
        ClaimsPrincipal user,
        PermissionGroupCode group,
        PermissionActionCode action)
    {
        var code = PermissionTaxonomy.BuildPermissionCode(group, action);
        var result = await authorizationService.AuthorizeAsync(user, AuthorizationPolicies.HasPermission(code));
        return result.Succeeded;
    }

    /// <summary>
    /// Same check as the <see cref="PermissionGroupCode"/> overload, but resolves the group from a
    /// vendor/source-or-destination-type enum value (e.g. a request's <c>SourceSystemType</c> or
    /// <c>DestinationType</c>) via <see cref="SourceSystemPermissionGroups.GroupFor"/> first — the exact same
    /// way <see cref="PermissionCatalog"/> resolved the permission at startup, so the two can never disagree
    /// about which group a given enum value belongs to.
    ///
    /// A value with no dedicated group (e.g. a destination type outside the curated set with its own
    /// View/Create/Edit/Delete/Execute permissions) has never had any permission of its own —
    /// <see cref="SourceSystemPermissionGroups.AllGroupsFor"/> deliberately never discovers/registers one
    /// for the generic fallback group, so probing it here would ask the authorization service for a
    /// policy that doesn't exist and throw "No policy found", not merely deny access. Such a value is
    /// therefore treated as ungated (allowed) — exactly the behavior it had before any RBAC existed for it.
    /// </summary>
    public static Task<bool> HasPermissionAsync(
        IAuthorizationService authorizationService,
        ClaimsPrincipal user,
        Enum sourceOrDestinationTypeValue,
        PermissionActionCode action)
    {
        if (!SourceSystemPermissionGroups.TryGroupFor(sourceOrDestinationTypeValue, out var group))
        {
            return Task.FromResult(true);
        }

        return HasPermissionAsync(authorizationService, user, group, action);
    }

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
        var allowed = await HasPermissionAsync(authorizationService, controller.User, group, action);
        return allowed ? null : controller.Forbid();
    }

    /// <summary>
    /// Same check as the <see cref="PermissionGroupCode"/> overload, but resolves the group from a
    /// vendor/source-type enum value (e.g. a request's <c>SourceSystemType</c>) via
    /// <see cref="SourceSystemPermissionGroups.GroupFor"/> first. This is the one call any controller
    /// needs for a <see cref="DynamicSourceSystemPermissionAttribute"/>-marked action — it resolves the
    /// exact same way <see cref="PermissionCatalog"/> resolved the permission at startup, so the two
    /// can never disagree about which group a given enum value belongs to.
    /// </summary>
    public static async Task<IActionResult?> AuthorizePermissionAsync(
        this ControllerBase controller,
        IAuthorizationService authorizationService,
        Enum sourceSystemValue,
        PermissionActionCode action)
    {
        var allowed = await HasPermissionAsync(authorizationService, controller.User, sourceSystemValue, action);
        return allowed ? null : controller.Forbid();
    }
}
