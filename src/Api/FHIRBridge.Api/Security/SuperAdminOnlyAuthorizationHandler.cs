using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class SuperAdminOnlyAuthorizationHandler : AuthorizationHandler<SuperAdminOnlyRequirement>
{
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserAccessRepository _userAccessRepository;

    public SuperAdminOnlyAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserAccessRepository userAccessRepository)
    {
        _currentUserService = currentUserService;
        _userAccessRepository = userAccessRepository;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SuperAdminOnlyRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // Backward-compatible path (RBAC redesign Step 1-2 preserved this deliberately): a JWT role
        // claim of SuperAdmin (or a legacy alias) still succeeds exactly as before, with no DB round
        // trip added on this path. Note this policy deliberately excludes plain "Admin" — unchanged.
        var claimRoles = CurrentUserClaimReader.GetRoles(context.User);
        if (claimRoles.Any(role => SuperAdminOnlyRequirement.AcceptedRoleNames.Contains(role)))
        {
            context.Succeed(requirement);
            return;
        }

        // RBAC redesign Step 3: a role's IsFullAccess flag (see Role.IsFullAccess) grants the same
        // access as the role-name check above, without ever consulting the role's name — the same
        // capability SuperAdmin has always had here (tenant CRUD, app-secret rotation, system settings,
        // etc. — see AllowedCorsOriginsController/AppSecretsController/SystemSettingsController/
        // TenantsController/SsoConfigurationsController/HapiTerminologyConfigurationController), now
        // available to a role without hardcoding its name.
        var userId = _currentUserService.CurrentUser.UserId;
        if (userId is null)
        {
            return;
        }

        var roles = await _userAccessRepository.GetUserRolesAsync(userId.Value, CancellationToken.None);
        if (roles.Any(r => r.IsFullAccess))
        {
            context.Succeed(requirement);
        }
    }
}
