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

    // Same proof as Allscripts above, for a second, brand-new SourceSystemType value (NewEHR) instead
    // of a pre-existing one — confirms the mechanism also covers vendors that don't exist yet today.
    [PermissionGroup("30000000-0000-0000-0000-000000000014", PermissionCategoryCode.Pipelines, "NewEHR")]
    NewEHR = 14,

    [PermissionGroup("30000000-0000-0000-0000-000000000015", PermissionCategoryCode.Platform, "Governance")]
    Governance = 15,

    // ── Source-type access (RBAC: restrict which source vendors a role can use) ─────────────────────────
    // Named to match SourceSystemType.Healow/.MeditechGreenfield/.GenericFhir/.Hl7v2/.Sample exactly, same
    // mechanism as Epic/Athenahealth/Cerner/Allscripts above — completes source-type coverage to all 9
    // SOURCES catalog entries so every Node Library source tile has its own dedicated Edit permission.
    [PermissionGroup("30000000-0000-0000-0000-000000000016", PermissionCategoryCode.Pipelines, "Healow")]
    Healow = 16,

    // DisplayName is "Meditech" (not the enum member's own "MeditechGreenfield") — display names are
    // free to differ from the member name and re-sync on their own (RbacBootstrapper/
    // SyncDiscoveredPermissionsAsync both already re-sync DisplayName whenever it drifts, no migration
    // needed), whereas the member NAME must stay "MeditechGreenfield" to keep resolving
    // SourceSystemType.MeditechGreenfield by name (see SourceSystemPermissionGroups).
    [PermissionGroup("30000000-0000-0000-0000-000000000017", PermissionCategoryCode.Pipelines, "Meditech")]
    MeditechGreenfield = 17,

    [PermissionGroup("30000000-0000-0000-0000-000000000018", PermissionCategoryCode.Pipelines, "Generic FHIR")]
    GenericFhir = 18,

    [PermissionGroup("30000000-0000-0000-0000-000000000019", PermissionCategoryCode.Pipelines, "Hl7v2")]
    Hl7v2 = 19,

    [PermissionGroup("30000000-0000-0000-0000-000000000020", PermissionCategoryCode.Pipelines, "Sample")]
    Sample = 20,

    // ── Destination-type access (RBAC: restrict which destination types a role can use) ────────────────
    // Named to match DestinationType.SqlServer/.AzureSql/.MySql/.PostgreSql/.Mongo/.Csv/.Sftp exactly —
    // SourceSystemPermissionGroups.GroupFor/AllGroupsFor resolve by name against ANY enum, not just
    // SourceSystemType, so these work with zero changes to that resolver. Limited to the 7 destination
    // types with a real form reachable from the Node Library canvas today; the other DestinationType
    // values are inert stub forms only reachable from the Settings admin page and stay on the generic
    // Configuration.Write permission.
    // Display names below are the polished, spaced/cased forms ("SQL Server", not the enum member's own
    // "SqlServer") — see the Meditech/GenericFhir comment above for why the member NAME must still match
    // DestinationType.SqlServer/.AzureSql/.MySql/.PostgreSql exactly while DisplayName is free to differ.
    [PermissionGroup("30000000-0000-0000-0000-000000000021", PermissionCategoryCode.Pipelines, "SQL Server")]
    SqlServer = 21,

    [PermissionGroup("30000000-0000-0000-0000-000000000022", PermissionCategoryCode.Pipelines, "Azure SQL")]
    AzureSql = 22,

    [PermissionGroup("30000000-0000-0000-0000-000000000023", PermissionCategoryCode.Pipelines, "MySQL")]
    MySql = 23,

    [PermissionGroup("30000000-0000-0000-0000-000000000024", PermissionCategoryCode.Pipelines, "PostgreSQL")]
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

    // Dedicated group for DestinationType.FhirRepository — the Node Library displays this type as
    // "Aidbox" (its real-world target product), but the enum/permission-code identity stays
    // "FhirRepository" to match the domain type exactly. Replaces the earlier `sourceconnections.*`
    // substitute a frontend-only edit had wired up for this type — that permission means "can reach
    // the Settings admin screen," an unrelated concern, and was never actually enforced server-side.
    [PermissionGroup("30000000-0000-0000-0000-000000000038", PermissionCategoryCode.Pipelines, "Aidbox")]
    FhirRepository = 38,

    [PermissionGroup("30000000-0000-0000-0000-000000000039", PermissionCategoryCode.Pipelines, "Medplum")]
    Medplum = 39,
}
