using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class UnifiedAdminAuthorizationHandler : AuthorizationHandler<UnifiedAdminRequirement>
{
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserAccessRepository _userAccessRepository;

    public UnifiedAdminAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserAccessRepository userAccessRepository)
    {
        _currentUserService = currentUserService;
        _userAccessRepository = userAccessRepository;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        UnifiedAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // Backward-compatible path (RBAC redesign Step 1-2 preserved this deliberately): a JWT role
        // claim of SuperAdmin/Admin (or a legacy alias) still succeeds exactly as before, with no DB
        // round trip added on this path.
        var claimRoles = CurrentUserClaimReader.GetRoles(context.User);
        if (claimRoles.Any(role => UnifiedAdminRequirement.AcceptedRoleNames.Contains(role)))
        {
            context.Succeed(requirement);
            return;
        }

        // RBAC redesign Step 3: a role's IsFullAccess flag (see Role.IsFullAccess) grants the same
        // access as the role-name check above, without ever consulting the role's name. This is what
        // lets a custom role marked "Full System Access" reach the ~50+ endpoints gated by this policy
        // that no permission code can reach (see PermissionsController, BulkExportJobsController, the
        // WorkflowEndpoints.cs run-diagnostics routes, etc.) — the same capability SuperAdmin/Admin have
        // always had here, now available to a role without hardcoding its name.
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
