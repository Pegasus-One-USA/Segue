using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Pre-create source endpoint discovery (SMART configuration + supported resource types + backend auth
/// scope probing) is part of building/editing a source connection, not a separate admin-only capability —
/// it was previously locked to UnifiedAdmin only, with no permission-based alternative, unlike the rest of
/// the source-connection surface (<c>SourceConnectionsController</c>, <c>ConfigurationsController</c>),
/// which is checked against <c>sourceconnections.create</c>/<c>.edit</c>. Succeeds on UnifiedAdmin (unchanged
/// prior behavior) OR either of those two permissions, so a role scoped to managing source connections can
/// use the wizard's Discover action without needing the Admin/SuperAdmin role.
/// </summary>
public sealed class SourceDiscoveryAccessAuthorizationHandler : AuthorizationHandler<SourceDiscoveryAccessRequirement>
{
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserPermissionsProvider _permissionsProvider;

    public SourceDiscoveryAccessAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserPermissionsProvider permissionsProvider)
    {
        _currentUserService = currentUserService;
        _permissionsProvider = permissionsProvider;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SourceDiscoveryAccessRequirement requirement)
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
        if (permissions.Contains(SourceDiscoveryAccessRequirement.SourceConnectionsCreateCode, StringComparer.OrdinalIgnoreCase)
            || permissions.Contains(SourceDiscoveryAccessRequirement.SourceConnectionsEditCode, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }
    }
}
