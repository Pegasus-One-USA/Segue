namespace FHIRBridge.Application.Security;

public static class AuthorizationPolicies
{
    /// <summary>Legacy role-based policy: requires GlobalAdmin or TenantAdmin role.</summary>
    public const string UnifiedAdmin = nameof(UnifiedAdmin);

    /// <summary>Prefix used to construct permission-based policy names.</summary>
    public const string PermissionPolicyPrefix = "HasPermission:";

    /// <summary>Returns the policy name for a given permission code, e.g. "HasPermission:user.view".</summary>
    public static string HasPermission(string permissionCode)
        => PermissionPolicyPrefix + permissionCode;
}
