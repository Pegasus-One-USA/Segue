namespace FHIRBridge.Application.Security;

/// <summary>
/// Standardized permission groups, each owned by exactly one <see cref="PermissionCategoryCode"/>. Adding a
/// new group means adding one enum member here with a <see cref="PermissionGroupAttribute"/> declaring its
/// stable Id, owning category, and display name — nothing else to touch, since <see cref="RbacSeedData.Groups"/>
/// is generated from this enum directly.
///
/// A member representing a source-connection vendor (Epic, Athenahealth, Cerner, ...) or a destination
/// type (SqlServer, Mongo, ...) must be named identically to its <c>SourceSystemType</c>/<c>DestinationType</c>
/// counterpart — <see cref="SourceSystemPermissionGroups"/> resolves the group for a dynamic permission check
/// by name against either enum, not a hand-maintained lookup table, so that adding the enum member here is the
/// only step needed for its permissions to be discovered.
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

    // 14 (NewEHR) intentionally removed — SourceSystemType.NewEHR/.NewEHRTwo are internal placeholder
    // enum members with no real vendor identity (see EHR_OPTIONS in the portal's
    // ehr-vendor-source-form.component.ts), never offered to a user anywhere in the app, so this group
    // showed up as a real-looking Access Control with no feature behind it. Removing the enum member is
    // enough on its own: SyncDiscoveredPermissionsAsync (Program.cs) deactivates its now-undiscovered
    // "newehr.*" permissions on next boot, and GetPermissionCatalogAsync already excludes any group with
    // no active permissions — no migration or manual data cleanup needed.

    [PermissionGroup("30000000-0000-0000-0000-000000000015", PermissionCategoryCode.Platform, "Governance")]
    Governance = 15,

    // ── Source-type access (RBAC: restrict which source vendors a role can use) ─────────────────────────
    // Named to match SourceSystemType.Healow/.MeditechGreenfield/.GenericFhir/.Sample exactly, same
    // mechanism as Epic/Athenahealth/Cerner/Allscripts above — completes source-type coverage to the
    // SOURCES catalog entries that have a real, working execution path, so every one of those Node Library
    // source tiles has its own dedicated Edit permission.
    [PermissionGroup("30000000-0000-0000-0000-000000000016", PermissionCategoryCode.Pipelines, "Healow")]
    Healow = 16,

    [PermissionGroup("30000000-0000-0000-0000-000000000017", PermissionCategoryCode.Pipelines, "MeditechGreenfield")]
    MeditechGreenfield = 17,

    [PermissionGroup("30000000-0000-0000-0000-000000000018", PermissionCategoryCode.Pipelines, "GenericFhir")]
    GenericFhir = 18,

    // 19 (Hl7v2) intentionally removed — unlike every other source-type group here, its workflow-canvas
    // node (Hl7v2MllpSourceNodeExecutor, in the Runtime engine) is a hardcoded stub: CreatePayload always
    // returns an empty ResourceBatch, in every environment, regardless of any real HL7 v2 traffic the MLLP
    // listener receives. There is no real execution path behind this Access Control to gate, unlike
    // Cerner/Allscripts/MeditechGreenfield above, which — though their own FHIR client isn't registered yet
    // either — at least have a genuine, tested save/configure path through the real API
    // (SourceConnectionPermissionTests). Same self-healing removal as NewEHR: no migration needed.

    [PermissionGroup("30000000-0000-0000-0000-000000000020", PermissionCategoryCode.Pipelines, "Sample")]
    Sample = 20,

    // ── Destination-type access (RBAC: restrict which destination types a role can use) ────────────────
    // Named to match DestinationType.SqlServer/.AzureSql/.MySql/.PostgreSql/.Mongo/.Csv/.Sftp exactly —
    // SourceSystemPermissionGroups.GroupFor/AllGroupsFor resolve by name against ANY enum, not just
    // SourceSystemType, so these work with zero changes to that resolver. Limited to the 7 destination
    // types with a real form reachable from the Node Library canvas today; the other DestinationType
    // values are inert stub forms only reachable from the Settings admin page and stay on the generic
    // Configuration.Write permission.
    [PermissionGroup("30000000-0000-0000-0000-000000000021", PermissionCategoryCode.Pipelines, "SqlServer")]
    SqlServer = 21,

    [PermissionGroup("30000000-0000-0000-0000-000000000022", PermissionCategoryCode.Pipelines, "AzureSql")]
    AzureSql = 22,

    [PermissionGroup("30000000-0000-0000-0000-000000000023", PermissionCategoryCode.Pipelines, "MySql")]
    MySql = 23,

    [PermissionGroup("30000000-0000-0000-0000-000000000024", PermissionCategoryCode.Pipelines, "PostgreSql")]
    PostgreSql = 24,

    [PermissionGroup("30000000-0000-0000-0000-000000000025", PermissionCategoryCode.Pipelines, "Mongo")]
    Mongo = 25,

    [PermissionGroup("30000000-0000-0000-0000-000000000026", PermissionCategoryCode.Pipelines, "Csv")]
    Csv = 26,

    [PermissionGroup("30000000-0000-0000-0000-000000000027", PermissionCategoryCode.Pipelines, "Sftp")]
    Sftp = 27,

    // ── Settings admin-screen access (RBAC: menu-level permission tree) ────────────────────────────────
    // Platform category, alongside Configuration/SourceConnections — these gate the Settings hub's own
    // admin CRUD screens, a different concern from the Pipelines-category vendor/type groups above (which
    // gate which source/destination TYPE a role can use inside a workflow, not whether they can reach the
    // Settings admin screen for it at all).
    [PermissionGroup("30000000-0000-0000-0000-000000000028", PermissionCategoryCode.Platform, "Destination Connections")]
    DestinationConnections = 28,

    [PermissionGroup("30000000-0000-0000-0000-000000000029", PermissionCategoryCode.Platform, "Mapping Profiles")]
    MappingProfiles = 29,

    [PermissionGroup("30000000-0000-0000-0000-000000000030", PermissionCategoryCode.Platform, "Transformation Rules")]
    TransformationRules = 30,

    [PermissionGroup("30000000-0000-0000-0000-000000000031", PermissionCategoryCode.Platform, "EHR Endpoints")]
    EhrEndpoints = 31,

    // Four independent groups, one per terminology-import controller (Loinc/Snomed/RxNorm/
    // Icd10ConfigurationController) — these briefly shared one "TerminologyCodes" group (itself split off
    // from the generic Configuration group Email still uses), which fixed the Email-vs-terminology coupling
    // but left LOINC/SNOMED/RxNorm/ICD-10 unable to be granted independently of each other, even though their
    // controllers, routes, services, and underlying data are all completely separate. Each terminology
    // system now has its own View/Write pair.
    [PermissionGroup("30000000-0000-0000-0000-000000000033", PermissionCategoryCode.Platform, "LOINC")]
    Loinc = 33,

    [PermissionGroup("30000000-0000-0000-0000-000000000034", PermissionCategoryCode.Platform, "SNOMED CT")]
    SnomedCt = 34,

    [PermissionGroup("30000000-0000-0000-0000-000000000035", PermissionCategoryCode.Platform, "RxNorm")]
    RxNorm = 35,

    [PermissionGroup("30000000-0000-0000-0000-000000000036", PermissionCategoryCode.Platform, "ICD-10")]
    Icd10 = 36,

    // Dedicated group for DestinationType.BlobStorage (Azure Blob Storage). Member name matches the enum
    // value exactly, so SourceSystemPermissionGroups.GroupFor resolves it directly instead of falling back
    // to SourceConnections — the existing [DynamicSourceSystemPermission(typeof(DestinationType), ...)]
    // attributes already cross the whole enum, so adding this group alone is sufficient to auto-discover
    // blobstorage.view/create/edit/delete/execute; no new attribute declarations are needed anywhere.
    [PermissionGroup("30000000-0000-0000-0000-000000000037", PermissionCategoryCode.Pipelines, "Blob Storage")]
    BlobStorage = 37,
}
