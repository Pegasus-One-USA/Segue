namespace FHIRBridge.Application.Security;

/// <summary>
/// Standardized permission groups, each owned by exactly one <see cref="PermissionCategoryCode"/> (see
/// <see cref="PermissionTaxonomy.GroupCategory"/>). Adding a new group means adding an enum member here
/// plus a taxonomy mapping, never a free-typed string.
/// </summary>
public enum PermissionGroupCode
{
    [PermissionDisplayName("User")]
    User = 1,

    [PermissionDisplayName("Role")]
    Role = 2,

    [PermissionDisplayName("Workflow")]
    Workflow = 3,

    [PermissionDisplayName("Configuration")]
    Configuration = 4,

    [PermissionDisplayName("Pipeline")]
    Pipeline = 5,

    [PermissionDisplayName("Audit Logs")]
    AuditLogs = 6,

    [PermissionDisplayName("Source Connections")]
    SourceConnections = 7,

    [PermissionDisplayName("Report")]
    Report = 8,

    [PermissionDisplayName("Payload")]
    Payload = 9,
}
