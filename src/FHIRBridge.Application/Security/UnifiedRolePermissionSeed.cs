namespace FHIRBridge.Application.Security;

/// <summary>
/// Single source of truth for the built-in platform role → permission grants. Consumed by both the EF
/// <c>HasData</c> seed and the in-memory repository so the two never drift.
/// </summary>
public static class UnifiedRolePermissionSeed
{
    /// <summary>
    /// Resolves a permission's id by its wire-format code (e.g. <see cref="UnifiedPermissions.UserInvite"/>)
    /// against <see cref="RbacSeedData.Permissions"/> — the id itself is never hand-picked here, it's
    /// whatever <see cref="PermissionTaxonomy.BuildPermissionId"/> derives for that permission's Group+Action.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Guid> PermissionIdsByCode =
        RbacSeedData.Permissions.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);

    private static Guid Id(string code) => PermissionIdsByCode[code];

    /// <summary>Every original platform permission id.</summary>
    private static readonly Guid[] OriginalPlatformPermissions =
    [
        Id(UnifiedPermissions.ConfigurationWrite),
        Id(UnifiedPermissions.PipelineExecute),
        Id(UnifiedPermissions.AuditLogsRead),
        Id(UnifiedPermissions.SourceConnectionsTest)
    ];

    /// <summary>Every user-module permission id (spec §4.1).</summary>
    private static readonly Guid[] UserModulePermissions =
    [
        Id(UnifiedPermissions.UserInvite),
        Id(UnifiedPermissions.UserView),
        Id(UnifiedPermissions.UserEdit),
        Id(UnifiedPermissions.UserDeactivate),
        Id(UnifiedPermissions.RoleCreate),
        Id(UnifiedPermissions.RoleEdit),
        Id(UnifiedPermissions.RoleDelete),
        Id(UnifiedPermissions.RoleAssign),
        Id(UnifiedPermissions.RoleView),
        Id(UnifiedPermissions.WorkflowCreate),
        Id(UnifiedPermissions.WorkflowEdit),
        Id(UnifiedPermissions.WorkflowDelete),
        Id(UnifiedPermissions.WorkflowRun),
        Id(UnifiedPermissions.WorkflowView),
        Id(UnifiedPermissions.ReportView),
        Id(UnifiedPermissions.PayloadView)
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
                Id(UnifiedPermissions.ConfigurationWrite),
                Id(UnifiedPermissions.PipelineExecute),
                Id(UnifiedPermissions.SourceConnectionsTest),
                Id(UnifiedPermissions.WorkflowCreate),
                Id(UnifiedPermissions.WorkflowEdit),
                Id(UnifiedPermissions.WorkflowDelete),
                Id(UnifiedPermissions.WorkflowRun),
                Id(UnifiedPermissions.WorkflowView),
                Id(UnifiedPermissions.PayloadView),
                Id(UnifiedPermissions.AuditLogsRead),
                Id(UnifiedPermissions.ReportView)
            ],

            [SeededSecurityIds.AuditRoleId] =
            [
                Id(UnifiedPermissions.AuditLogsRead),
                Id(UnifiedPermissions.WorkflowView),
                Id(UnifiedPermissions.ReportView)
            ]
        };
}
