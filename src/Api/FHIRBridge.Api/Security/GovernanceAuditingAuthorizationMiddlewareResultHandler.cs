using FHIRBridge.Governance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Wraps the framework's default authorization result handler to write one <see cref="AuthorizationLog"/>-backed
/// entry whenever a permission-based (<see cref="PermissionRequirement"/>) policy is denied — the single hook
/// point for RBAC denial logging, so no controller/endpoint has to remember to log it itself. Deliberately only
/// logs <see cref="PolicyAuthorizationResult.Forbidden"/> (authenticated user, insufficient permission), not
/// <see cref="PolicyAuthorizationResult.Challenged"/> (unauthenticated) — the latter is an authentication
/// concern already covered by <c>AuthenticationLog</c>.
/// </summary>
public sealed class GovernanceAuditingAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            var permissionCode = policy.Requirements.OfType<PermissionRequirement>().FirstOrDefault()?.PermissionCode;
            if (permissionCode is not null)
            {
                var governanceLogger = context.RequestServices.GetRequiredService<IGovernanceLogger>();
                await governanceLogger.LogAuthorizationAsync(
                    new AuthorizationEntry(context.Request.Path, permissionCode, "Denied"),
                    context.RequestAborted);
            }
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
