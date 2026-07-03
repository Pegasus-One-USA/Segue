using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class UnifiedAdminAuthorizationHandler : AuthorizationHandler<UnifiedAdminRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        UnifiedAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        var claimRoles = CurrentUserClaimReader.GetRoles(context.User);
        if (claimRoles.Any(role => UnifiedAdminRequirement.AcceptedRoleNames.Contains(role)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
