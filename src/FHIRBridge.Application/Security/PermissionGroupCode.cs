namespace FHIRBridge.Application.Security;

/// <summary>
/// Standardized permission groups, each owned by exactly one <see cref="PermissionCategoryCode"/>. Adding a
/// new group means adding one enum member here with a <see cref="PermissionGroupAttribute"/> declaring its
/// stable Id, owning category, and display name — nothing else to touch, since <see cref="RbacSeedData.Groups"/>
/// is generated from this enum directly.
/// </summary>
public enum PermissionGroupCode
{
    [PermissionGroup("30000000-0000-0000-0000-000000000004", PermissionCategoryCode.AccessControl, "User")]
    User = 1,

    [PermissionGroup("30000000-0000-0000-0000-000000000005", PermissionCategoryCode.AccessControl, "Role")]
    Role = 2,

    [PermissionGroup("30000000-0000-0000-0000-000000000006", PermissionCategoryCode.Platform, "Workflow")]
    Workflow = 3,

    [PermissionGroup("30000000-0000-0000-0000-000000000001", PermissionCategoryCode.Platform, "Configuration")]
    Configuration = 4,

    [PermissionGroup("30000000-0000-0000-0000-000000000002", PermissionCategoryCode.Platform, "Pipeline")]
    Pipeline = 5,

    [PermissionGroup("30000000-0000-0000-0000-000000000003", PermissionCategoryCode.Platform, "Audit Logs")]
    AuditLogs = 6,

    [PermissionGroup("30000000-0000-0000-0000-000000000009", PermissionCategoryCode.Platform, "Source Connections")]
    SourceConnections = 7,

    [PermissionGroup("30000000-0000-0000-0000-000000000007", PermissionCategoryCode.Platform, "Report")]
    Report = 8,

    [PermissionGroup("30000000-0000-0000-0000-000000000008", PermissionCategoryCode.Platform, "Payload")]
    Payload = 9,

    [PermissionGroup("30000000-0000-0000-0000-000000000010", PermissionCategoryCode.Pipelines, "Epic")]
    Epic = 10,

    [PermissionGroup("30000000-0000-0000-0000-000000000011", PermissionCategoryCode.Pipelines, "Athena")]
    Athena = 11,
}
