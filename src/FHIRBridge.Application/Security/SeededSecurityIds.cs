namespace FHIRBridge.Application.Security;

public static class SeededSecurityIds
{
    // The first three GUIDs are preserved from the original SuperAdmin/Admin/Operator roles so existing
    // seeded data (user-role and role-permission links) keeps resolving after the rename to the five-role model.
    public static readonly Guid GlobalAdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid TenantAdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid PipelineEngineerRoleId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid AnalystRoleId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    public static readonly Guid AuditorRoleId = Guid.Parse("10000000-0000-0000-0000-000000000005");

    public static readonly Guid TenantsReadPermissionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid TenantsWritePermissionId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid ConfigurationWritePermissionId = Guid.Parse("20000000-0000-0000-0000-000000000003");
    public static readonly Guid PipelineExecutePermissionId = Guid.Parse("20000000-0000-0000-0000-000000000004");
    public static readonly Guid AuditLogsReadPermissionId = Guid.Parse("20000000-0000-0000-0000-000000000005");
    public static readonly Guid SourceConnectionsTestPermissionId = Guid.Parse("20000000-0000-0000-0000-000000000006");
}
