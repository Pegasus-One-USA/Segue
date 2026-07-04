namespace FHIRBridge.Application.Security;

public static class SeededSecurityIds
{
    // ── Platform roles (preserved from original seed) ────────────────────────
    public static readonly Guid SuperAdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid AdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid OperationsRoleId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid AuditRoleId = Guid.Parse("10000000-0000-0000-0000-000000000005");

    // Permission ids are no longer hand-picked here — every Permission's id is derived deterministically
    // from its Group+Action pair (see PermissionTaxonomy.BuildPermissionId), so a new permission never
    // needs a manually-minted Guid, and the same action reused under a different group (e.g. View under
    // both User and Role) naturally gets a distinct id instead of colliding.

    // ── Permission groups (formerly "permission categories") ─────────────────
    public static readonly Guid ConfigurationGroupId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    public static readonly Guid PipelineGroupId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    public static readonly Guid AuditLogsGroupId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    public static readonly Guid UserGroupId = Guid.Parse("30000000-0000-0000-0000-000000000004");
    public static readonly Guid RoleGroupId = Guid.Parse("30000000-0000-0000-0000-000000000005");
    public static readonly Guid WorkflowGroupId = Guid.Parse("30000000-0000-0000-0000-000000000006");
    public static readonly Guid ReportGroupId = Guid.Parse("30000000-0000-0000-0000-000000000007");
    public static readonly Guid PayloadGroupId = Guid.Parse("30000000-0000-0000-0000-000000000008");
    public static readonly Guid SourceConnectionsGroupId = Guid.Parse("30000000-0000-0000-0000-000000000009");

    // ── Permission categories (top tier, above groups) ────────────────────────
    public static readonly Guid AccessControlCategoryId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    public static readonly Guid PlatformCategoryId = Guid.Parse("40000000-0000-0000-0000-000000000002");
}
