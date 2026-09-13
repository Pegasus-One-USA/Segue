using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// The permission catalog (every permission that exists, grouped/labeled) is read-only reference metadata,
/// not tenant configuration — it's needed by the Role Permissions screen's read-only grid, reachable with
/// role.view alone (see user-management.routes.ts), not just full Admins. Succeeds on the UnifiedAdmin role
/// (unchanged prior behavior — GetAll on this same controller keeps that exact gate) OR the "role.view"
/// permission, so a role.view-only caller can actually load the grid that screen promises instead of the
/// whole request failing and the page rendering "Role not found".
/// </summary>
public sealed class PermissionCatalogAccessAuthorizationHandler : AuthorizationHandler<PermissionCatalogAccessRequirement>
{
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserPermissionsProvider _permissionsProvider;

    public PermissionCatalogAccessAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserPermissionsProvider permissionsProvider)
    {
        _currentUserService = currentUserService;
        _permissionsProvider = permissionsProvider;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionCatalogAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var claimRoles = CurrentUserClaimReader.GetRoles(context.User);
        if (claimRoles.Any(role => UnifiedAdminRequirement.AcceptedRoleNames.Contains(role)))
        {
            context.Succeed(requirement);
            return;
        }

        var userId = _currentUserService.CurrentUser.UserId;
        if (userId is null)
        {
            return;
        }

        var permissions = await _permissionsProvider.GetEffectivePermissionCodesAsync(userId.Value, CancellationToken.None);
        if (permissions.Contains(PermissionCatalogAccessRequirement.RoleViewCode, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }
    }
}
