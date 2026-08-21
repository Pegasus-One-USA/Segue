namespace FHIRBridge.Application.Security;

public static class AuthorizationPolicies
{
    /// <summary>Legacy role-based policy: requires SuperAdmin or Admin role.</summary>
    public const string UnifiedAdmin = nameof(UnifiedAdmin);

    /// <summary>Strict role-based policy: requires SuperAdmin — unlike <see cref="UnifiedAdmin"/>, a
    /// regular Admin does not satisfy this. For actions too sensitive to delegate to Admin (e.g.
    /// force-disabling another user's MFA without a code).</summary>
    public const string SuperAdminOnly = nameof(SuperAdminOnly);

    /// <summary>
    /// View-level access to the Workflow module: satisfied by the literal <c>workflow.view</c> permission
    /// OR any permission on a workflow node (a source vendor or destination type, e.g. <c>epic.view</c>,
    /// <c>sqlserver.edit</c>) — see <c>WorkflowModuleAccessAuthorizationHandler</c>. Deliberately NOT used
    /// for workflow CRUD/operation endpoints (build, copy, run, delete), which keep requiring their own
    /// literal <c>workflow.create</c>/<c>edit</c>/<c>delete</c>/<c>run</c> code independently of node
    /// permissions — a node permission implies module ACCESS, never module CRUD.
    /// </summary>
    public const string WorkflowModuleAccess = nameof(WorkflowModuleAccess);

    /// <summary>Prefix used to construct permission-based policy names.</summary>
    public const string PermissionPolicyPrefix = "HasPermission:";

    /// <summary>Returns the policy name for a given permission code, e.g. "HasPermission:user.view".</summary>
    public static string HasPermission(string permissionCode)
        => PermissionPolicyPrefix + permissionCode;
}
