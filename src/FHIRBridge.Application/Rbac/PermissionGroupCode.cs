namespace FHIRBridge.Application.Security;

/// <summary>
/// Standardized permission groups, each owned by exactly one <see cref="PermissionCategoryCode"/>. Adding a
/// new group means adding one enum member here with a <see cref="PermissionGroupAttribute"/> declaring its
/// stable Id, owning category, and display name — nothing else to touch, since <see cref="RbacSeedData.Groups"/>
/// is generated from this enum directly.
///
/// A member representing a source-connection vendor (Epic, Athenahealth, Cerner, ...) must be named
/// identically to its <c>SourceSystemType</c> counterpart — <see cref="SourceSystemPermissionGroups"/>
/// resolves the group for a dynamic permission check by name, not a hand-maintained lookup table, so
/// that adding the enum member here is the only step needed for its permissions to be discovered.
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

    [PermissionGroup("30000000-0000-0000-0000-000000000009", PermissionCategoryCode.Platform, "Source Connections")]
    SourceConnections = 7,

    [PermissionGroup("30000000-0000-0000-0000-000000000007", PermissionCategoryCode.Platform, "Report")]
    Report = 8,

    [PermissionGroup("30000000-0000-0000-0000-000000000008", PermissionCategoryCode.Platform, "Payload")]
    Payload = 9,

    [PermissionGroup("30000000-0000-0000-0000-000000000010", PermissionCategoryCode.Pipelines, "Epic")]
    Epic = 10,

    // Named to match SourceSystemType.Athenahealth exactly — SourceSystemPermissionGroups resolves a
    // vendor's permission group by name, so this member's name IS the connection to that source type.
    [PermissionGroup("30000000-0000-0000-0000-000000000011", PermissionCategoryCode.Pipelines, "Athenahealth")]
    Athenahealth = 11,

    [PermissionGroup("30000000-0000-0000-0000-000000000012", PermissionCategoryCode.Pipelines, "Cerner")]
    Cerner = 12,

    // Proves the auto-discovery: this is the only line added anywhere to give Allscripts (already a
    // real SourceSystemType with no dedicated permission group) its own "allscripts.edit" permission.
    [PermissionGroup("30000000-0000-0000-0000-000000000013", PermissionCategoryCode.Pipelines, "Allscripts")]
    Allscripts = 13,

    // Same proof as Allscripts above, for a second, brand-new SourceSystemType value (NewEHR) instead
    // of a pre-existing one — confirms the mechanism also covers vendors that don't exist yet today.
    [PermissionGroup("30000000-0000-0000-0000-000000000014", PermissionCategoryCode.Pipelines, "NewEHR")]
    NewEHR = 14,

    [PermissionGroup("30000000-0000-0000-0000-000000000015", PermissionCategoryCode.Platform, "Governance")]
    Governance = 15,
}
