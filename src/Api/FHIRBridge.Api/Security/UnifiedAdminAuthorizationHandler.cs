using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class UnifiedAdminAuthorizationHandler : AuthorizationHandler<UnifiedAdminRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUserAccessRepository _userAccessRepository;

    public UnifiedAdminAuthorizationHandler(
        IHttpContextAccessor httpContextAccessor,
        IUserAccessRepository userAccessRepository)
    {
        _httpContextAccessor = httpContextAccessor;
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

        var claimRoles = CurrentUserClaimReader.GetRoles(context.User);
        if (claimRoles.Any(role => UnifiedAdminRequirement.AcceptedRoleNames.Contains(role)))
        {
            context.Succeed(requirement);
            return;
        }

        var tenantId = TryGetTenantId();
        if (!tenantId.HasValue)
        {
            return;
        }

        var externalUserId = CurrentUserClaimReader.GetExternalUserId(context.User);
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return;
        }

        var hasTenantAdminRole = await _userAccessRepository.HasTenantRoleAsync(
            tenantId.Value,
            externalUserId,
            [UnifiedRoles.SuperAdmin, UnifiedRoles.Admin],
            CancellationToken.None);

        if (hasTenantAdminRole)
        {
            context.Succeed(requirement);
        }
    }

    private Guid? TryGetTenantId()
    {
        var routeValues = _httpContextAccessor.HttpContext?.Request.RouteValues;
        if (routeValues is null ||
            !routeValues.TryGetValue("tenantId", out var value) ||
            value is null)
        {
            return null;
        }

        return Guid.TryParse(value.ToString(), out var tenantId)
            ? tenantId
            : null;
    }
}
