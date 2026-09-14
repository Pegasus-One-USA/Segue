using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Succeeds on the literal generic code (e.g. <c>sourceconnections.view</c>) OR any vendor-specific
/// permission for the same action — <c>epic.view</c>, <c>athenahealth.view</c>, ... for
/// <see cref="PermissionGroupCode.SourceConnections"/>; <c>sqlserver.view</c>, <c>mongo.view</c>, ... for
/// <see cref="PermissionGroupCode.DestinationConnections"/>. The vendor groups checked are exactly
/// <see cref="SourceSystemPermissionGroups.AllGroupsFor"/>'s own enumeration over <see cref="SourceSystemType"/>
/// / <see cref="DestinationType"/> — the same list <see cref="PermissionCatalog"/> already used to
/// auto-discover those permissions in the first place, so this can never drift from what actually exists in
/// the catalog.
///
/// Where an action has no vendor-specific equivalent at all (e.g. <c>Export</c>/<c>Deactivate</c> — no
/// <c>[DynamicSourceSystemPermission]</c> site ever declares them, so no <c>epic.export</c>/
/// <c>sqlserver.deactivate</c> permission exists to hold), the vendor check below simply never matches —
/// behavior for those actions is unchanged from a plain generic-only check.
///
/// This only ever widens the FIRST of two independent gates several endpoints already have (see
/// <c>SourceConnectionsController</c>/<c>ConfigurationsController</c>'s per-instance
/// <c>AuthorizePermissionAsync(resolvedVendorType, action)</c> calls) — those inline checks still require the
/// SPECIFIC connection's own vendor permission and are completely unaffected by this handler.
/// </summary>
public sealed class GenericConnectionPermissionAuthorizationHandler
    : AuthorizationHandler<GenericConnectionPermissionRequirement>
{
    private static readonly Lazy<HashSet<PermissionGroupCode>> SourceVendorGroups = new(() =>
        new HashSet<PermissionGroupCode>(SourceSystemPermissionGroups.AllGroupsFor(typeof(SourceSystemType))));

    private static readonly Lazy<HashSet<PermissionGroupCode>> DestinationVendorGroups = new(() =>
        new HashSet<PermissionGroupCode>(SourceSystemPermissionGroups.AllGroupsFor(typeof(DestinationType))));

    private readonly ICurrentUserService _currentUserService;
    private readonly IUserPermissionsProvider _permissionsProvider;

    public GenericConnectionPermissionAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserPermissionsProvider permissionsProvider)
    {
        _currentUserService = currentUserService;
        _permissionsProvider = permissionsProvider;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GenericConnectionPermissionRequirement requirement)
    {
        var userId = _currentUserService.CurrentUser.UserId;
        if (userId is null)
        {
            return;
        }

        var permissions = await _permissionsProvider.GetEffectivePermissionCodesAsync(userId.Value, CancellationToken.None);

        if (permissions.Contains(requirement.GenericCode, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
            return;
        }

        var vendorGroups = requirement.GenericGroup == PermissionGroupCode.SourceConnections
            ? SourceVendorGroups.Value
            : DestinationVendorGroups.Value;

        foreach (var vendorGroup in vendorGroups)
        {
            var vendorCode = PermissionTaxonomy.BuildPermissionCode(vendorGroup, requirement.Action);
            if (permissions.Contains(vendorCode, StringComparer.OrdinalIgnoreCase))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}
