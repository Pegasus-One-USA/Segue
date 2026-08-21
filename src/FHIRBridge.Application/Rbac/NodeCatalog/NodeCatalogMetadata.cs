using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Rbac.NodeCatalog;

/// <summary>
/// The one hand-authored registry of presentation metadata (display name, subtitle, category, icon,
/// color) and real-implementation status for every <see cref="SourceSystemType"/> and
/// <see cref="DestinationType"/> value — replaces the four places this used to be scattered across
/// (portal's <c>sources.data.ts</c>, <c>transforms.data.ts</c>, <c>TRANSFORM_META</c>, and
/// <c>PhaseConfigService</c>'s <c>enabledSourceIds</c>/<c>enabledTransformIds</c> arrays).
///
/// <see cref="ValidateCompleteness"/> is called once at startup (see <c>Program.cs</c>) specifically so
/// that adding a new <see cref="SourceSystemType"/>/<see cref="DestinationType"/> member without adding
/// a matching entry here fails the boot immediately with a clear error, instead of silently producing
/// an incomplete Node Catalog entry (missing display name/implemented flag) the first time a client asks.
/// </summary>
public static class NodeCatalogMetadata
{
    public sealed record Entry(
        string DisplayName,
        string? Subtitle,
        string? Category,
        string? Icon,
        string? Color,
        /// <summary>
        /// A permanent technical fact, not a rollout/business decision, and not merely "has a
        /// permission group" or "has a form that saves successfully": does this type support the
        /// COMPLETE workflow lifecycle today — Node Library → Configure → Save → Reload → Execute?
        /// All five steps must genuinely work, not just the first four. Node Library visibility is
        /// exactly <c>Implemented &amp;&amp; user has &lt;node&gt;.view</c> — see NodeCatalogBuilder.
        /// This is deliberately NOT a "Phase 1/2" staging flag: there is no separate rollout gate
        /// anywhere in the Node Catalog, and it is NOT the same axis as the Permission Catalog — a
        /// type can have a full set of real RBAC permissions (view/create/edit/delete/execute) while
        /// still being <see langword="false"/> here, and those permissions must never be removed just
        /// because this flag is false (see e.g. Cerner/Allscripts/Healow/MeditechGreenfield/Hl7v2/NewEHR/
        /// NewEHRTwo below, all of which keep their permission groups untouched). A type only ever
        /// flips this to <see langword="true"/> once every one of the five lifecycle steps is verified —
        /// see AzureSql/Sftp for the "save works but silently coerces to a sibling type" counter-example,
        /// and Cerner/Allscripts/Healow/MeditechGreenfield below for the "configure and save work, but
        /// execution throws" counter-example.
        /// </summary>
        bool Implemented);

    // ── Implemented set — reflects verified, complete-lifecycle support only (real dedicated form, a
    // save path that persists this exact enum value, a correct reload, AND real runtime execution
    // support — not merely a permission group existing or a form that saves without error). Do not
    // change any Implemented value below as a side effect of unrelated edits to this file: that would
    // change Node Library visibility. Flipping a value to true requires re-verifying all five lifecycle
    // steps, not just that a form component exists — see AzureSql/Sftp's comments below, and Cerner/
    // Allscripts/Healow/MeditechGreenfield/Hl7v2's. ─────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<SourceSystemType, Entry> SourceEntries = new Dictionary<SourceSystemType, Entry>
    {
        // Epic, Athenahealth, and GenericFhir are the only sources with COMPLETE lifecycle support
        // verified end-to-end: real SOURCE_FORM_REGISTRY form, a save path that persists this exact
        // SourceSystemType, a correct reload, AND real runtime execution — all three are registered in
        // FhirSourceClientFactory.DefaultRegistrations (Runtime/FHIRBridge.Runtime.Infrastructure/
        // Connectors/FhirSourceClientFactory.cs), so a workflow built against one of them can actually run.
        [SourceSystemType.Epic] = new(
            "Epic", "Epic on FHIR — full connection wizard.", "Backend system", "EP", "#ff5a4f", Implemented: true),
        [SourceSystemType.Athenahealth] = new(
            "Athenahealth", "athenahealth FHIR R4 APIs.", "Provider (standalone)", "ATH", "#7b2ff7", Implemented: true),
        [SourceSystemType.GenericFhir] = new(
            "Generic FHIR R4", "Any conformant FHIR R4 server.", "Backend system", "R4", "#5b6573", Implemented: true),
        // Cerner/Allscripts/Healow/MeditechGreenfield: Configure/Save/Reload genuinely work (real form,
        // correct SourceSystemType persisted since the buildSource() fix, correct reload) — but Execute
        // does not: FhirSourceClientFactory.DefaultRegistrations has all four commented out ("GATED
        // (SQL/CSV phase)... re-enable them here once the generic Source hierarchy + ApplicationType
        // axis land"), and the Runtime Plane's own node catalog (DefaultWorkflowNodeCatalog.Items) has
        // no palette entry for any of them either. A workflow built with one of these sources cannot
        // currently be run. Implemented is therefore false — this does NOT touch their RBAC permission
        // groups (cerner.view/create/edit/delete/execute etc. stay exactly as they are; Permission
        // Catalog is a separate axis from Node Catalog — see this file's own Entry.Implemented doc
        // comment). Revisit once FhirSourceClientFactory registers real clients for these four.
        [SourceSystemType.Cerner] = new(
            "Cerner (Oracle Health)", "Oracle Health Millennium FHIR R4.", "Backend system", "OH", "#1175bb", Implemented: false),
        [SourceSystemType.Allscripts] = new(
            "Allscripts (Veradigm)", "Veradigm / Allscripts FHIR R4.", "Provider (standalone)", "ALS", "#0aa1a1", Implemented: false),
        [SourceSystemType.Healow] = new(
            "Healow (eClinicalWorks)", "eClinicalWorks Healow FHIR R4.", "Patient (standalone)", "HEL", "#e8590c", Implemented: false),
        [SourceSystemType.MeditechGreenfield] = new(
            "Meditech Greenfield", "Meditech Greenfield FHIR R4.", "Backend system", "MED", "#2f6f4f", Implemented: false),
        // Hl7v2: excluded for a different, more fundamental reason than the four above — its MLLP
        // host/port/facility configuration has no representation anywhere in the SourceConnection
        // domain model (BaseUrl + OAuth/JWT SourceAuthenticationConfiguration only), so Configure/Save
        // don't genuinely work for it either, not just Execute. The real HL7v2 MLLP listener
        // (Hl7MllpListenerService/Hl7MllpOptions) is a separate, single-instance, appsettings-driven
        // mechanism unrelated to SourceConnection, and per its own doc comment is not even registered
        // as a hosted service today. WorkflowBuildAssemblerService.buildSource() now throws explicitly
        // for an Hl7v2 connector rather than fabricating a request — see that file.
        [SourceSystemType.Hl7v2] = new(
            "HL7 v2 / MLLP", "HL7 v2 over MLLP (ingest + map).", "Backend system", "HL7", "#b45309", Implemented: false),
        [SourceSystemType.Sample] = new(
            "Sample (sandbox)", "Synthetic sample data for testing.", "Backend system", "SMP", "#8b8f98", Implemented: true),
        // Demo-only values proving the auto-discovery mechanism (see PermissionGroupCode.NewEHR) — no
        // real connection form exists for either, so both stay unconditionally Implemented: false. Do
        // NOT flip these just because they can carry RBAC permissions (newehr.view/create/...) — a
        // permission group existing is never sufficient on its own; see this record's own doc comment.
        [SourceSystemType.NewEHR] = new(
            "NewEHR", null, null, null, null, Implemented: false),
        [SourceSystemType.NewEHRTwo] = new(
            "NewEHRTwo", null, null, null, null, Implemented: false),
    };

    private static readonly IReadOnlyDictionary<DestinationType, Entry> DestinationEntries = new Dictionary<DestinationType, Entry>
    {
        // These eight are the only destination types the destination wizard's own save-time type
        // resolution can actually produce (verified by direct code inspection of
        // destination-wizard.component.ts's provisionDestinationConnection() — each has its own
        // distinct, non-colliding branch there, unlike AzureSql/Sftp below), AND each has a real,
        // registered writer in ConfiguredDestinationWriterFactory.DefaultRegistrations, so Execute
        // genuinely works too. Not inferred from a form component or permission group existing alone
        // (see the Node Catalog investigation report).
        [DestinationType.SqlServer] = new(
            "SQL Server", "Write to Microsoft SQL Server.", "Relational", "SQL", "#CC2927", Implemented: true),
        [DestinationType.Csv] = new(
            "CSV", "Emit CSV files.", "File", "CSV", "#374151", Implemented: true),
        [DestinationType.MySql] = new(
            "MySQL", "Write to MySQL.", "Relational", "MY", "#4479A1", Implemented: true),
        [DestinationType.Mongo] = new(
            "MongoDB", "Write to a MongoDB collection.", "NoSQL", "MDB", "#47A248", Implemented: true),
        [DestinationType.PostgreSql] = new(
            "PostgreSQL", "Write to PostgreSQL.", "Relational", "PG", "#336791", Implemented: true),
        [DestinationType.FhirRepository] = new(
            "Aidbox", "POST a transaction bundle to a FHIR store.", "Cloud / FHIR", "FHR", "#00A89D", Implemented: true),
        [DestinationType.Medplum] = new(
            "Medplum (FHIR)", "Write FHIR resources to a Medplum store.", "Cloud / FHIR", "MP", "#00A89D", Implemented: true),
        [DestinationType.BlobStorage] = new(
            "Azure Blob Storage", "Write objects to Azure Blob.", "Cloud / FHIR", "BLB", "#0089D6", Implemented: true),
        // NOT implemented, despite having a real form component reachable via the picker: the wizard's
        // 'sql' family always saves DestinationType.SqlServer, never AzureSql — selecting "Azure SQL"
        // from any entry point that reached this far would silently persist a SqlServer row. Do not flip
        // this to true by adding a routing/wizard-type entry alone; it needs the wizard to actually be
        // able to choose and persist AzureSql distinctly first (separate piece of work).
        [DestinationType.AzureSql] = new(
            "Azure SQL", "Write to Azure SQL Database.", "Relational", "AZS", "#0078D4", Implemented: false),
        [DestinationType.Snowflake] = new(
            "Snowflake", "Load into Snowflake.", "Analytics", "SNW", "#29B5E8", Implemented: false),
        [DestinationType.PowerBi] = new(
            "Power BI", "Push to a Power BI dataset.", "Analytics", "PBI", "#F2C811", Implemented: false),
        [DestinationType.Tableau] = new(
            "Tableau", "Publish to Tableau.", "Analytics", "TAB", "#E97627", Implemented: false),
        [DestinationType.Databricks] = new(
            "Databricks", "Load into Databricks.", "Analytics", "DBR", "#FF3621", Implemented: false),
        [DestinationType.S3] = new(
            "Amazon S3", "Write objects to Amazon S3.", "Cloud / FHIR", "S3", "#FF9900", Implemented: false),
        [DestinationType.Excel] = new(
            "Excel", "Emit .xlsx workbooks.", "File", "XLS", "#217346", Implemented: false),
        [DestinationType.Ndjson] = new(
            "NDJSON", "Emit newline-delimited JSON.", "File", "NDJ", "#475569", Implemented: false),
        [DestinationType.Parquet] = new(
            "Parquet", "Emit columnar Parquet.", "File", "PAR", "#64748B", Implemented: false),
        [DestinationType.Avro] = new(
            "Avro", "Emit Avro records.", "File", "AVR", "#64748B", Implemented: false),
        [DestinationType.Protobuf] = new(
            "Protobuf", "Emit Protobuf messages.", "File", "PRT", "#64748B", Implemented: false),
        [DestinationType.Pdf] = new(
            "PDF Report", "Render a PDF report.", "File", "PDF", "#DC2626", Implemented: false),
        // NOT implemented, for the same reason as AzureSql above: the wizard's 'csv' family always
        // saves DestinationType.Csv, never Sftp.
        [DestinationType.Sftp] = new(
            "SFTP", "Deliver files over SFTP.", "Delivery", "FTP", "#475569", Implemented: false),
        [DestinationType.RestApi] = new(
            "REST API", "POST to an outbound REST endpoint.", "Delivery", "API", "#475569", Implemented: false),
        [DestinationType.InMemory] = new(
            "In-memory (test)", "Sink for testing — discards output.", "Delivery", "MEM", "#94A3B8", Implemented: false),
    };

    public static bool TryGetSource(SourceSystemType type, out Entry entry) => SourceEntries.TryGetValue(type, out entry!);

    public static bool TryGetDestination(DestinationType type, out Entry entry) => DestinationEntries.TryGetValue(type, out entry!);

    /// <summary>
    /// Throws if any <see cref="SourceSystemType"/> or <see cref="DestinationType"/> value has no
    /// registered entry above. Called once at startup — see this class's own doc comment for why a
    /// missing entry must fail the boot rather than surface as a silently incomplete node later.
    /// </summary>
    public static void ValidateCompleteness()
    {
        var missing = new List<string>();

        foreach (SourceSystemType value in Enum.GetValues<SourceSystemType>())
        {
            if (!SourceEntries.ContainsKey(value))
            {
                missing.Add($"SourceSystemType.{value}");
            }
        }

        foreach (DestinationType value in Enum.GetValues<DestinationType>())
        {
            if (!DestinationEntries.ContainsKey(value))
            {
                missing.Add($"DestinationType.{value}");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "NodeCatalogMetadata is missing an entry for: " + string.Join(", ", missing) +
                ". Add a corresponding entry to NodeCatalogMetadata before this type can be used — " +
                "the Node Catalog refuses to produce an incomplete node rather than guessing metadata.");
        }
    }
}
