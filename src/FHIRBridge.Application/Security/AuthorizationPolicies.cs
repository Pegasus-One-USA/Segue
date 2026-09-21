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

    /// <summary>
    /// Read access to the FHIR element catalog (resource types + fields) — reference metadata, not tenant
    /// configuration, so it's gated more loosely than the rest of <c>MappingController</c>. Satisfied by
    /// UnifiedAdmin OR the "transformationrules.write" permission (see
    /// <c>MappingCatalogAccessAuthorizationHandler</c>), so the Transformation Rules editor's field picker
    /// works for a user who can edit rules without being a full Admin.
    /// </summary>
    public const string MappingCatalogAccess = nameof(MappingCatalogAccess);

    /// <summary>
    /// Pre-create source endpoint discovery (SMART configuration + resource types + backend auth scope
    /// probing) — part of building/editing a source connection, not a separate admin-only capability.
    /// Satisfied by UnifiedAdmin OR "sourceconnections.create"/"sourceconnections.edit" (see
    /// <c>SourceDiscoveryAccessAuthorizationHandler</c>), so a role scoped to managing source connections
    /// can use the wizard's Discover action without needing the Admin/SuperAdmin role.
    /// </summary>
    public const string SourceDiscoveryAccess = nameof(SourceDiscoveryAccess);

    /// <summary>
    /// Read access to the full permission catalog (every permission that exists, grouped/labeled) — read-only
    /// reference metadata, not tenant configuration. Satisfied by UnifiedAdmin OR the "role.view" permission
    /// (see <c>PermissionCatalogAccessAuthorizationHandler</c>), so the Role Permissions screen's read-only
    /// grid (reachable with role.view alone) can actually load for a non-Admin viewer. <c>GetAll</c> on the
    /// same controller keeps requiring UnifiedAdmin directly, unaffected by this policy.
    /// </summary>
    public const string PermissionCatalogAccess = nameof(PermissionCatalogAccess);

    /// <summary>Prefix used to construct permission-based policy names.</summary>
    public const string PermissionPolicyPrefix = "HasPermission:";

    /// <summary>Returns the policy name for a given permission code, e.g. "HasPermission:user.view".</summary>
    public static string HasPermission(string permissionCode)
        => PermissionPolicyPrefix + permissionCode;
}
