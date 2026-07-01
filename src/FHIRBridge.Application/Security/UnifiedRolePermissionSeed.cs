namespace FHIRBridge.Application.Security;

/// <summary>
/// Single source of truth for the built-in five-role → permission grants. Consumed by both the EF
/// <c>HasData</c> seed and the in-memory repository so the two never drift.
/// </summary>
public static class UnifiedRolePermissionSeed
{
    private static readonly Guid[] AllPermissions =
    [
        SeededSecurityIds.TenantsReadPermissionId,
        SeededSecurityIds.TenantsWritePermissionId,
        SeededSecurityIds.ConfigurationWritePermissionId,
        SeededSecurityIds.PipelineExecutePermissionId,
        SeededSecurityIds.AuditLogsReadPermissionId,
        SeededSecurityIds.SourceConnectionsTestPermissionId
    ];

    /// <summary>Built-in role id → granted permission ids.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Grants { get; } =
        new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [SeededSecurityIds.GlobalAdminRoleId] = AllPermissions,
            [SeededSecurityIds.TenantAdminRoleId] = AllPermissions,
            [SeededSecurityIds.PipelineEngineerRoleId] =
            [
                SeededSecurityIds.TenantsReadPermissionId,
                SeededSecurityIds.ConfigurationWritePermissionId,
                SeededSecurityIds.PipelineExecutePermissionId,
                SeededSecurityIds.SourceConnectionsTestPermissionId
            ],
            [SeededSecurityIds.AnalystRoleId] =
            [
                SeededSecurityIds.TenantsReadPermissionId,
                SeededSecurityIds.PipelineExecutePermissionId,
                SeededSecurityIds.AuditLogsReadPermissionId
            ],
            [SeededSecurityIds.AuditorRoleId] =
            [
                SeededSecurityIds.TenantsReadPermissionId,
                SeededSecurityIds.AuditLogsReadPermissionId
            ]
        };
}
