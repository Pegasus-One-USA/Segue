using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>Marker requirement for <see cref="AuthorizationPolicies.SourceDiscoveryAccess"/> — the actual
/// UnifiedAdmin-role-OR-permission check lives in <see cref="SourceDiscoveryAccessAuthorizationHandler"/>.</summary>
public sealed class SourceDiscoveryAccessRequirement : IAuthorizationRequirement
{
    /// <summary>Discovery has no vendor context yet (it runs off a bare FHIR base URL, before any source
    /// connection object exists — see <c>SourceDiscoveryController</c>'s own doc comment), so it can't be
    /// gated per-vendor the way an actual connection's CRUD is. Anyone who can create or edit a source
    /// connection at all should be able to use the wizard's Discover action.</summary>
    public static readonly string SourceConnectionsCreateCode =
        PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.SourceConnections, PermissionActionCode.Create);

    public static readonly string SourceConnectionsEditCode =
        PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.SourceConnections, PermissionActionCode.Edit);
}
