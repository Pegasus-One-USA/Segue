namespace FHIRBridge.Application.Security;

public static class AuthorizationPolicies
{
    /// <summary>Legacy role-based policy: requires SuperAdmin or Admin role.</summary>
    public const string UnifiedAdmin = nameof(UnifiedAdmin);

    /// <summary>Strict role-based policy: requires SuperAdmin — unlike <see cref="UnifiedAdmin"/>, a
    /// regular Admin does not satisfy this. For actions too sensitive to delegate to Admin (e.g.
    /// force-disabling another user's MFA without a code).</summary>
    public const string SuperAdminOnly = nameof(SuperAdminOnly);

    /// <summary>Prefix used to construct permission-based policy names.</summary>
    public const string PermissionPolicyPrefix = "HasPermission:";

    /// <summary>Returns the policy name for a given permission code, e.g. "HasPermission:user.view".</summary>
    public static string HasPermission(string permissionCode)
        => PermissionPolicyPrefix + permissionCode;
}
