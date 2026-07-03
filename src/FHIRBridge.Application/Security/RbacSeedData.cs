namespace FHIRBridge.Application.Security;

/// <summary>
/// Canonical, code-side definition of the built-in RBAC reference data (permissions, system roles, and their
/// role→permission grants). This is the single source of truth consumed by the runtime RBAC bootstrapper that
/// self-provisions a freshly-migrated database on boot. It is the exact data that previously lived in the EF
/// <c>HasData</c> migration seed — same GUIDs, names, descriptions, categories, and mappings — moved from a
/// migration-time seed to an idempotent runtime bootstrap.
/// </summary>
public static class RbacSeedData
{
    /// <summary>A built-in permission row (metadata only; GUID + name + category + description).</summary>
    public sealed record PermissionSeed(Guid Id, string Name, string Description, string Category);

    /// <summary>A built-in role row.</summary>
    public sealed record RoleSeed(Guid Id, string Name, string Description);

    /// <summary>
    /// The 20 built-in platform permissions, in seed order. Category/description are preserved verbatim
    /// from the former <c>PermissionConfiguration.HasData</c> block.
    /// </summary>
    public static IReadOnlyList<PermissionSeed> Permissions { get; } =
    [
        // Original platform permissions.
        new(SeededSecurityIds.ConfigurationWritePermissionId, UnifiedPermissions.ConfigurationWrite, "Manage source, destination, mapping, webhook, and route configuration.", "Configuration"),
        new(SeededSecurityIds.PipelineExecutePermissionId, UnifiedPermissions.PipelineExecute, "Execute configured pipeline routes.", "Pipeline"),
        new(SeededSecurityIds.AuditLogsReadPermissionId, UnifiedPermissions.AuditLogsRead, "Read operational audit logs.", "Audit"),
        new(SeededSecurityIds.SourceConnectionsTestPermissionId, UnifiedPermissions.SourceConnectionsTest, "Test source system connectivity.", "Configuration"),

        // User module permissions.
        new(SeededSecurityIds.UserInvitePermissionId, UnifiedPermissions.UserInvite, "Invite a new user to the organization.", "User"),
        new(SeededSecurityIds.UserViewPermissionId, UnifiedPermissions.UserView, "View the list of users.", "User"),
        new(SeededSecurityIds.UserEditPermissionId, UnifiedPermissions.UserEdit, "Update a user's profile information.", "User"),
        new(SeededSecurityIds.UserDeactivatePermissionId, UnifiedPermissions.UserDeactivate, "Deactivate a user account.", "User"),

        // Role module permissions.
        new(SeededSecurityIds.RoleCreatePermissionId, UnifiedPermissions.RoleCreate, "Create a new custom role.", "Role"),
        new(SeededSecurityIds.RoleEditPermissionId, UnifiedPermissions.RoleEdit, "Edit an existing role.", "Role"),
        new(SeededSecurityIds.RoleDeletePermissionId, UnifiedPermissions.RoleDelete, "Delete a custom role.", "Role"),
        new(SeededSecurityIds.RoleAssignPermissionId, UnifiedPermissions.RoleAssign, "Assign or remove roles from users.", "Role"),
        new(SeededSecurityIds.RoleViewPermissionId, UnifiedPermissions.RoleView, "View roles and their permissions.", "Role"),

        // Workflow module permissions.
        new(SeededSecurityIds.WorkflowCreatePermissionId, UnifiedPermissions.WorkflowCreate, "Create a new workflow.", "Workflow"),
        new(SeededSecurityIds.WorkflowEditPermissionId, UnifiedPermissions.WorkflowEdit, "Edit an existing workflow.", "Workflow"),
        new(SeededSecurityIds.WorkflowDeletePermissionId, UnifiedPermissions.WorkflowDelete, "Delete a workflow.", "Workflow"),
        new(SeededSecurityIds.WorkflowRunPermissionId, UnifiedPermissions.WorkflowRun, "Execute a workflow.", "Workflow"),
        new(SeededSecurityIds.WorkflowViewPermissionId, UnifiedPermissions.WorkflowView, "View workflow details.", "Workflow"),

        // Report / payload permissions.
        new(SeededSecurityIds.ReportViewPermissionId, UnifiedPermissions.ReportView, "View reports and analytics.", "Report"),
        new(SeededSecurityIds.PayloadViewPermissionId, UnifiedPermissions.PayloadView, "View data payloads from workflow runs.", "Payload"),
    ];

    /// <summary>
    /// The 4 built-in system roles, in seed order. Descriptions are preserved verbatim from the former
    /// <c>RoleConfiguration.HasData</c> block.
    /// </summary>
    public static IReadOnlyList<RoleSeed> Roles { get; } =
    [
        new(SeededSecurityIds.SuperAdminRoleId, UnifiedRoles.SuperAdmin, "Full platform administrator."),
        new(SeededSecurityIds.AdminRoleId, UnifiedRoles.Admin, "Administers configuration and users."),
        new(SeededSecurityIds.OperationsRoleId, UnifiedRoles.Operations, "Builds and runs pipeline configurations, and reviews data and audit output."),
        new(SeededSecurityIds.AuditRoleId, UnifiedRoles.Audit, "Read-only access to configuration and audit logs."),
    ];

    /// <summary>
    /// Built-in role id → granted permission ids. Delegates to <see cref="UnifiedRolePermissionSeed.Grants"/>
    /// so the runtime bootstrap and the in-memory repository never drift.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> RolePermissions => UnifiedRolePermissionSeed.Grants;
}
