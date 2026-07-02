namespace FHIRBridge.Application.Security;

public static class SeededSecurityIds
{
    // ── Platform roles (preserved from original seed) ────────────────────────
    public static readonly Guid SuperAdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid AdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid OperationsRoleId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid AuditRoleId = Guid.Parse("10000000-0000-0000-0000-000000000005");

    // ── Original platform permissions ────────────────────────────────────────
    public static readonly Guid TenantsReadPermissionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid TenantsWritePermissionId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid ConfigurationWritePermissionId = Guid.Parse("20000000-0000-0000-0000-000000000003");
    public static readonly Guid PipelineExecutePermissionId = Guid.Parse("20000000-0000-0000-0000-000000000004");
    public static readonly Guid AuditLogsReadPermissionId = Guid.Parse("20000000-0000-0000-0000-000000000005");
    public static readonly Guid SourceConnectionsTestPermissionId = Guid.Parse("20000000-0000-0000-0000-000000000006");

    // ── User module permissions (spec §4.1) ──────────────────────────────────
    public static readonly Guid UserInvitePermissionId = Guid.Parse("20000000-0000-0000-0001-000000000001");
    public static readonly Guid UserViewPermissionId = Guid.Parse("20000000-0000-0000-0001-000000000002");
    public static readonly Guid UserEditPermissionId = Guid.Parse("20000000-0000-0000-0001-000000000003");
    public static readonly Guid UserDeactivatePermissionId = Guid.Parse("20000000-0000-0000-0001-000000000004");

    // ── Role module permissions ───────────────────────────────────────────────
    public static readonly Guid RoleCreatePermissionId = Guid.Parse("20000000-0000-0000-0002-000000000001");
    public static readonly Guid RoleEditPermissionId = Guid.Parse("20000000-0000-0000-0002-000000000002");
    public static readonly Guid RoleDeletePermissionId = Guid.Parse("20000000-0000-0000-0002-000000000003");
    public static readonly Guid RoleAssignPermissionId = Guid.Parse("20000000-0000-0000-0002-000000000004");
    public static readonly Guid RoleViewPermissionId = Guid.Parse("20000000-0000-0000-0002-000000000005");

    // ── Workflow module permissions ───────────────────────────────────────────
    public static readonly Guid WorkflowCreatePermissionId = Guid.Parse("20000000-0000-0000-0003-000000000001");
    public static readonly Guid WorkflowEditPermissionId = Guid.Parse("20000000-0000-0000-0003-000000000002");
    public static readonly Guid WorkflowDeletePermissionId = Guid.Parse("20000000-0000-0000-0003-000000000003");
    public static readonly Guid WorkflowRunPermissionId = Guid.Parse("20000000-0000-0000-0003-000000000004");
    public static readonly Guid WorkflowViewPermissionId = Guid.Parse("20000000-0000-0000-0003-000000000005");

    // ── Tenant module permissions ─────────────────────────────────────────────
    public static readonly Guid TenantSettingsEditPermissionId = Guid.Parse("20000000-0000-0000-0004-000000000001");
    public static readonly Guid TenantBillingViewPermissionId = Guid.Parse("20000000-0000-0000-0004-000000000002");

    // ── Report / payload permissions ─────────────────────────────────────────
    public static readonly Guid ReportViewPermissionId = Guid.Parse("20000000-0000-0000-0005-000000000001");
    public static readonly Guid PayloadViewPermissionId = Guid.Parse("20000000-0000-0000-0006-000000000001");
}
