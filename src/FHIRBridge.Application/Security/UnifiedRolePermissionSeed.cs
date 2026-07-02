namespace FHIRBridge.Application.Security;

/// <summary>
/// Single source of truth for the built-in platform role → permission grants. Consumed by both the EF
/// <c>HasData</c> seed and the in-memory repository so the two never drift.
/// </summary>
public static class UnifiedRolePermissionSeed
{
    /// <summary>Every original platform permission id.</summary>
    private static readonly Guid[] OriginalPlatformPermissions =
    [
        SeededSecurityIds.TenantsReadPermissionId,
        SeededSecurityIds.TenantsWritePermissionId,
        SeededSecurityIds.ConfigurationWritePermissionId,
        SeededSecurityIds.PipelineExecutePermissionId,
        SeededSecurityIds.AuditLogsReadPermissionId,
        SeededSecurityIds.SourceConnectionsTestPermissionId
    ];

    /// <summary>Every user-module permission id (spec §4.1).</summary>
    private static readonly Guid[] UserModulePermissions =
    [
        SeededSecurityIds.UserInvitePermissionId,
        SeededSecurityIds.UserViewPermissionId,
        SeededSecurityIds.UserEditPermissionId,
        SeededSecurityIds.UserDeactivatePermissionId,
        SeededSecurityIds.RoleCreatePermissionId,
        SeededSecurityIds.RoleEditPermissionId,
        SeededSecurityIds.RoleDeletePermissionId,
        SeededSecurityIds.RoleAssignPermissionId,
        SeededSecurityIds.RoleViewPermissionId,
        SeededSecurityIds.WorkflowCreatePermissionId,
        SeededSecurityIds.WorkflowEditPermissionId,
        SeededSecurityIds.WorkflowDeletePermissionId,
        SeededSecurityIds.WorkflowRunPermissionId,
        SeededSecurityIds.WorkflowViewPermissionId,
        SeededSecurityIds.TenantSettingsEditPermissionId,
        SeededSecurityIds.TenantBillingViewPermissionId,
        SeededSecurityIds.ReportViewPermissionId,
        SeededSecurityIds.PayloadViewPermissionId
    ];

    /// <summary>All platform + user-module permissions combined.</summary>
    private static readonly Guid[] AllPermissions =
    [
        ..OriginalPlatformPermissions,
        ..UserModulePermissions
    ];

    /// <summary>Built-in platform role id → granted permission ids.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Grants { get; } =
        new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            // SuperAdmin and Admin receive every permission.
            [SeededSecurityIds.SuperAdminRoleId] = AllPermissions,
            [SeededSecurityIds.AdminRoleId] = AllPermissions,

            // Operations covers the former PipelineEngineer + Analyst duties (build/run
            // pipelines and workflows, plus review audit logs and reports).
            [SeededSecurityIds.OperationsRoleId] =
            [
                SeededSecurityIds.TenantsReadPermissionId,
                SeededSecurityIds.ConfigurationWritePermissionId,
                SeededSecurityIds.PipelineExecutePermissionId,
                SeededSecurityIds.SourceConnectionsTestPermissionId,
                SeededSecurityIds.WorkflowCreatePermissionId,
                SeededSecurityIds.WorkflowEditPermissionId,
                SeededSecurityIds.WorkflowDeletePermissionId,
                SeededSecurityIds.WorkflowRunPermissionId,
                SeededSecurityIds.WorkflowViewPermissionId,
                SeededSecurityIds.PayloadViewPermissionId,
                SeededSecurityIds.AuditLogsReadPermissionId,
                SeededSecurityIds.ReportViewPermissionId
            ],

            [SeededSecurityIds.AuditRoleId] =
            [
                SeededSecurityIds.TenantsReadPermissionId,
                SeededSecurityIds.AuditLogsReadPermissionId,
                SeededSecurityIds.WorkflowViewPermissionId,
                SeededSecurityIds.ReportViewPermissionId
            ]
        };
}
