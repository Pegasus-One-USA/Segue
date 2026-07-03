namespace FHIRBridge.Application.Security;

public static class UnifiedPermissions
{
    // ── Platform / pipeline permissions (original) ───────────────────────────
    public const string ConfigurationWrite = "configuration.write";
    public const string PipelineExecute = "pipeline.execute";
    public const string AuditLogsRead = "auditlogs.read";
    public const string SourceConnectionsTest = "sourceconnections.test";

    // ── User module permissions (spec §4.1) ──────────────────────────────────
    public const string UserInvite = "user.invite";
    public const string UserView = "user.view";
    public const string UserEdit = "user.edit";
    public const string UserDeactivate = "user.deactivate";

    // ── Role module permissions ───────────────────────────────────────────────
    public const string RoleCreate = "role.create";
    public const string RoleEdit = "role.edit";
    public const string RoleDelete = "role.delete";
    public const string RoleAssign = "role.assign";
    public const string RoleView = "role.view";

    // ── Workflow module permissions ───────────────────────────────────────────
    public const string WorkflowCreate = "workflow.create";
    public const string WorkflowEdit = "workflow.edit";
    public const string WorkflowDelete = "workflow.delete";
    public const string WorkflowRun = "workflow.run";
    public const string WorkflowView = "workflow.view";

    // ── Report / payload permissions ─────────────────────────────────────────
    public const string ReportView = "report.view";
    public const string PayloadView = "payload.view";
}
