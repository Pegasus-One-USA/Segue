using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// The FHIR element catalog (resource types + their fields) is read-only reference metadata, not tenant
/// configuration — it's needed by any screen with a field picker, not just full Admins. Succeeds on the
/// UnifiedAdmin role (unchanged prior behavior) OR the "transformationrules.write" permission, so a user who
/// can edit transformation rules (but isn't a GlobalAdmin/TenantAdmin) can still populate the rule editor's
/// "Source field" dropdown instead of it silently falling back to no real fields at all.
/// </summary>
public sealed class MappingCatalogAccessAuthorizationHandler : AuthorizationHandler<MappingCatalogAccessRequirement>
{
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserPermissionsProvider _permissionsProvider;

    public MappingCatalogAccessAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserPermissionsProvider permissionsProvider)
    {
        _currentUserService = currentUserService;
        _permissionsProvider = permissionsProvider;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MappingCatalogAccessRequirement requirement)
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
        if (permissions.Contains(MappingCatalogAccessRequirement.TransformationRulesWriteCode, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }
    }
}
