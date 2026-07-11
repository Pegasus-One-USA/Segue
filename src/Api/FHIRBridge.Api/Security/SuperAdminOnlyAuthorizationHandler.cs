using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class SuperAdminOnlyAuthorizationHandler : AuthorizationHandler<SuperAdminOnlyRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SuperAdminOnlyRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        var claimRoles = CurrentUserClaimReader.GetRoles(context.User);
        if (claimRoles.Any(role => SuperAdminOnlyRequirement.AcceptedRoleNames.Contains(role)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
