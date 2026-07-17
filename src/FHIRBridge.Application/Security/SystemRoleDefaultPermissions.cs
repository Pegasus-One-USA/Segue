namespace FHIRBridge.Application.Security;

/// <summary>
/// Default permission grants for the built-in system roles (SuperAdmin, Admin, Operations, Audit). Consumed
/// via <see cref="RbacSeedData.RolePermissions"/> by both the EF-backed runtime bootstrap and the in-memory
/// repository, so the two never drift.
/// </summary>
public static class SystemRoleDefaultPermissions
{
    /// <summary>
    /// Resolves a permission's id by its (Group, Action) pair against <see cref="RbacSeedData.PermissionIdsByGroupAction"/> —
    /// the id itself is never hand-picked here, it's whatever <see cref="PermissionTaxonomy.BuildPermissionId"/>
    /// derives for that pair.
    /// </summary>
    private static Guid Id(PermissionGroupCode group, PermissionActionCode action)
    {
        return RbacSeedData.PermissionIdsByGroupAction[(group, action)];
    }

    /// <summary>
    /// Every permission that exists. SuperAdmin and Admin always get all of them, derived straight from
    /// <see cref="RbacSeedData.Permissions"/> — a newly added permission reaches both roles automatically,
    /// nobody has to remember to add it to a parallel "all permissions" list here.
    /// </summary>
    private static readonly Guid[] AllPermissions = RbacSeedData.Permissions.Select(p => p.Id).ToArray();

    /// <summary>Built-in role id → granted permission ids.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Grants { get; } =
        new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            // SuperAdmin and Admin receive every permission that exists.
            [SeededSecurityIds.SuperAdminRoleId] = AllPermissions,
            [SeededSecurityIds.AdminRoleId] = AllPermissions,

            // Operations covers the former PipelineEngineer + Analyst duties (build/run
            // pipelines and workflows, plus review audit logs and reports).
            [SeededSecurityIds.OperationsRoleId] =
            [
                Id(PermissionGroupCode.Configuration, PermissionActionCode.Write),
                Id(PermissionGroupCode.Pipeline, PermissionActionCode.Execute),
                Id(PermissionGroupCode.SourceConnections, PermissionActionCode.Test),
                Id(PermissionGroupCode.Workflow, PermissionActionCode.Create),
                Id(PermissionGroupCode.Workflow, PermissionActionCode.Edit),
                Id(PermissionGroupCode.Workflow, PermissionActionCode.Delete),
                Id(PermissionGroupCode.Workflow, PermissionActionCode.Run),
                Id(PermissionGroupCode.Workflow, PermissionActionCode.View),
                Id(PermissionGroupCode.Payload, PermissionActionCode.View),
                Id(PermissionGroupCode.Report, PermissionActionCode.View)
            ],

            [SeededSecurityIds.AuditRoleId] =
            [
                Id(PermissionGroupCode.Workflow, PermissionActionCode.View),
                Id(PermissionGroupCode.Report, PermissionActionCode.View)
            ]
        };
}
