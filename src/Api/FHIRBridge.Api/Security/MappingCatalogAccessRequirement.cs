using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>Marker requirement for <see cref="AuthorizationPolicies.MappingCatalogAccess"/> — the actual
/// UnifiedAdmin-role-OR-permission check lives in <see cref="MappingCatalogAccessAuthorizationHandler"/>.</summary>
public sealed class MappingCatalogAccessRequirement : IAuthorizationRequirement
{
    /// <summary>Any caller who can read/write transformation rules also needs the field catalog — that's
    /// what drives the "Source field" picker on the rule-editor screen.</summary>
    public static readonly string TransformationRulesWriteCode =
        PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.TransformationRules, PermissionActionCode.Write);
}
