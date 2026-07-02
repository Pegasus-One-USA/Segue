using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Typed replacement for <c>[Authorize(Policy = "HasPermission:code")]</c>. Ties an
/// action/controller directly to a permission code so <see cref="PermissionCatalog"/>
/// can discover it via reflection and cross-check it against <see cref="UnifiedPermissions"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class StandardPermissionAttribute : AuthorizeAttribute
{
    public StandardPermissionAttribute(string permissionCode)
        : base(AuthorizationPolicies.HasPermission(permissionCode))
    {
        PermissionCode = permissionCode;
    }

    public string PermissionCode { get; }
}
