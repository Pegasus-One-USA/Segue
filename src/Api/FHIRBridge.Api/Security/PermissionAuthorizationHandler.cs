using FHIRBridge.Application.Abstractions.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserPermissionsProvider _permissionsProvider;

    public PermissionAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserPermissionsProvider permissionsProvider)
    {
        _currentUserService = currentUserService;
        _permissionsProvider = permissionsProvider;
    }

    // Permission codes are resolved per-request from IUserPermissionsProvider (DB-backed, short-lived
    // cache) rather than trusted from a JWT claim — the JWT no longer carries them at all (see
    // JwtAccessTokenIssuer). Only the small "uid" claim is needed here to key the lookup.
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userId = _currentUserService.CurrentUser.UserId;
        if (userId is null)
        {
            return;
        }

        var permissions = await _permissionsProvider.GetEffectivePermissionCodesAsync(userId.Value, CancellationToken.None);

        if (permissions.Contains(requirement.PermissionCode, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }
    }
}
