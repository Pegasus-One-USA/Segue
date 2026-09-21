using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Catalog;

public sealed class DefaultWorkflowNodeCatalog : IWorkflowNodeCatalog
{
    /// <summary>Destinations that persist whole FHIR resources rather than mapped relational rows — see
    /// MappingNodeExecutor's <c>wholeResourceFhir</c> branch, which already treats all three alike.
    /// <c>WorkflowGraphValidator.DestinationRequiresMappedRecords</c> reads this same set, so the "no upstream
    /// Mapping node required" exemption and the widened input contracts below can never drift apart.
    ///
    /// Declared BEFORE <see cref="Items"/>: static field initializers run in textual order, and Destination()
    /// reads this set while Items is being built — below Items it would still be null at that point.</summary>
    internal static readonly HashSet<string> WholeResourceFhirDestinationNodeTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            WorkflowNodeTypes.FhirRepositoryDestination,
            WorkflowNodeTypes.MedplumDestination,
            WorkflowNodeTypes.AzureFhirServiceDestination,
        };

    private static readonly WorkflowNodeCatalogItem[] Items =
    [
        Source(WorkflowNodeTypes.EpicSource),
        // Every vendor listed here gets its OWN NodeType on a newly saved workflow, so a run's node history and
        // audit rows name the EHR the data actually came from instead of reading "EpicSourceNode" for all of them.
        // A vendor may only be listed once BOTH exist: an IFhirSourceClient in FhirSourceClientFactory.
        // DefaultRegistrations, and its executor in WorkflowInfrastructureServiceCollectionExtensions — this list
        // is what WorkflowGraphValidator accepts at run time, so listing a vendor with no registered client would
        // turn a rejected save into a failed run.
        Source(WorkflowNodeTypes.AthenahealthSource),
        Source(WorkflowNodeTypes.EClinicalWorksSource),
        // STILL GATED: executors exist, but no IFhirSourceClient is registered for these three, so a node saved
        // under their NodeType would throw at client selection. They keep falling back to EpicSourceNode (see
        // RouteToWorkflowGraphProjection.MapSourceNodeType and the portal's transformIdForNode) until each gets a
        // client — a thin FhirSourceConnectorBase subclass, as GenericFhirSourceClient shows — registered here.
        // Source(WorkflowNodeTypes.CernerSource),
        // Source(WorkflowNodeTypes.AllscriptsSource),
        // Source(WorkflowNodeTypes.MeditechSource),
        Source(WorkflowNodeTypes.GenericFhirSource),
        // Source(WorkflowNodeTypes.Hl7v2MllpSource),
        Source(WorkflowNodeTypes.SampleSource),
        Compliance(WorkflowNodeTypes.Consent, "consent", "Consent", "Apply configured consent policy.", 10, [WorkflowDataContract.ResourceBatch], WorkflowDataContract.ResourceBatch),
        Compliance(WorkflowNodeTypes.UsCoreValidation, "fhir-validation", "FHIR Validation", "Validate resources against US Core / base R4 profiles.", 20, [WorkflowDataContract.ResourceBatch], WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.Normalization, "normalize", "Normalize Data", "Normalize FHIR resources for downstream transforms.", 30, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.FlattenExtensions, "flatten-extensions", "Flatten Extensions", "Flatten FHIR extensions into mappable fields.", 31, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.DataQualityScoring, "data-quality", "Data Quality Scoring", "Score resource completeness and data quality.", 32, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.PatientMatching, "patient-matching", "Patient Matching (MPI)", "Match patients against a master patient index.", 33, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.FhirResourceTransform, "transformation", "Transformation", "Apply transformation rules to FHIR resources before they are written.", 34, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.Terminology, "terminology", "Terminology Mapping", "Validate / translate ICD, SNOMED, LOINC, RxNorm codes.", 40, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyValidate, "terminology-validate", "Terminology Validate", "Validate coded values against configured terminology services.", 41, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyLookup, "terminology-lookup", "Terminology Lookup", "Lookup display and metadata for coded values.", 42, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyTranslate, "terminology-translate", "Terminology Translate", "Translate coded values between code systems.", 43, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyExpand, "terminology-expand", "Terminology Expand", "Expand value sets for downstream validation.", 44, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        // Accepts raw resources as well as normalized ones: de-identification reads whatever
        // ReadResourceEnvelopes hands it (both shapes carry ResourceEnvelopes), and a pipeline may redact
        // straight off the source without a normalization step in front.
        Compliance(
            WorkflowNodeTypes.DeIdentification,
            "deid-safeharbor",
            "De-identification",
            "Apply HIPAA de-identification policy when required.",
            50,
            [WorkflowDataContract.ResourceBatch, WorkflowDataContract.NormalizedResourceBatch],
            WorkflowDataContract.DeIdentifiedBatch),
        Transform(
            WorkflowNodeTypes.Mapping,
            "field-mapping",
            "Field Mapping",
            "Map FHIR paths to destination fields.",
            60,
            [WorkflowDataContract.ResourceBatch, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.DeIdentifiedBatch],
            WorkflowDataContract.MappedRecordBatch),
        Transform(WorkflowNodeTypes.RepeatingArrayMapping, "repeating-array-mapping", "Repeating Array Mapping", "Expand repeating mapped values.", 61, WorkflowDataContract.MappedRecordBatch, WorkflowDataContract.MappedRecordBatch),
        Destination(WorkflowNodeTypes.SqlServerDestination),
        Destination(WorkflowNodeTypes.CsvDestination),
        Destination(WorkflowNodeTypes.MySqlDestination),
        Destination(WorkflowNodeTypes.MongoDestination),
        Destination(WorkflowNodeTypes.PostgreSqlDestination),
        Destination(WorkflowNodeTypes.MedplumDestination),
        Destination(WorkflowNodeTypes.FhirRepositoryDestination),
        Destination(WorkflowNodeTypes.AzureFhirServiceDestination),
        Destination(WorkflowNodeTypes.BlobDestination),
        Destination(WorkflowNodeTypes.DataLakeWebhookDestination),
        Destination(WorkflowNodeTypes.DataFabricAzureDestination),
        Destination(WorkflowNodeTypes.DataFabricWarehouseDestination),
        // GATED (SQL/CSV phase): only SqlServer + CSV + MySql + Mongo + PostgreSql + Medplum + FhirRepository +
        // AzureFhirService + Blob destinations are exposed in the palette. The writers below remain registered in
        // ConfiguredDestinationWriterFactory and can be re-listed here as each is productized.
        // Destination(WorkflowNodeTypes.AzureSqlDestination),
        // Destination(WorkflowNodeTypes.SnowflakeDestination),
        // Destination(WorkflowNodeTypes.PowerBiDestination),
        // Destination(WorkflowNodeTypes.TableauDestination),
        // Destination(WorkflowNodeTypes.DatabricksDestination),
        // Destination(WorkflowNodeTypes.S3Destination),
        // Destination(WorkflowNodeTypes.ExcelDestination),
        // Destination(WorkflowNodeTypes.NdjsonDestination),
        // Destination(WorkflowNodeTypes.ParquetDestination),
        // Destination(WorkflowNodeTypes.AvroDestination),
        // Destination(WorkflowNodeTypes.ProtobufDestination),
        // Destination(WorkflowNodeTypes.PdfDestination),
        // Destination(WorkflowNodeTypes.SftpDestination),
        // Destination(WorkflowNodeTypes.RestApiDestination),
        // Destination(WorkflowNodeTypes.InMemoryDestination),
        new(
            WorkflowNodeTypes.AuditLineage,
            WorkflowNodeCategory.Compliance,
            80,
            [],
            [WorkflowDataContract.DestinationWriteResult],
            WorkflowDataContract.AuditResult,
            WorkflowNodeTypes.AuditLineage,
            "Audit & Lineage",
            "audit-lineage",
            "Hash-chained audit and record-level lineage."),
        // GATED (not yet in scope): WebhookNotifierNodeExecutor is fully implemented and registered (see
        // WorkflowInfrastructureServiceCollectionExtensions.AddWorkflowInfrastructure) but not listed in the
        // catalog, so it never appears in the /workflow-catalog palette. Re-add this entry (rank 71 — one above
        // SqlServerDestination/CsvDestination's rank 70) once the capability is productized.
        // new(
        //     WorkflowNodeTypes.WebhookNotifier,
        //     WorkflowNodeCategory.Destination,
        //     71,
        //     [],
        //     [WorkflowDataContract.DestinationWriteResult],
        //     WorkflowDataContract.DestinationWriteResult,
        //     WorkflowNodeTypes.WebhookNotifier,
        //     "HTTP Notify (Webhook)",
        //     "http-notify",
        //     "Notify a webhook once an upstream destination finishes writing."),
        Analytics(WorkflowNodeTypes.HedisMeasureReport),
        Analytics(WorkflowNodeTypes.AnomalyDetection),
        Analytics(WorkflowNodeTypes.PatientAggregation)
    ];

    public IReadOnlyCollection<WorkflowNodeCatalogItem> List() => Items;

    public WorkflowNodeCatalogItem? Find(string nodeType)
        => Items.FirstOrDefault(item => string.Equals(item.NodeType, nodeType, StringComparison.OrdinalIgnoreCase));

    private static WorkflowNodeCatalogItem Source(string nodeType)
        => new(
            nodeType,
            WorkflowNodeCategory.Source,
            0,
            [],
            [],
            WorkflowDataContract.ResourceBatch,
            nodeType,
            DisplayNameFor(nodeType),
            TransformIdFor(nodeType),
            DescriptionFor(nodeType));

    private static WorkflowNodeCatalogItem Transform(
        string nodeType,
        string transformId,
        string displayName,
        string description,
        int rank,
        WorkflowDataContract inputContract,
        WorkflowDataContract outputContract,
        IReadOnlyCollection<string>? requiredConfigurationFields = null)
        => Transform(nodeType, transformId, displayName, description, rank, [inputContract], outputContract, requiredConfigurationFields);

    private static WorkflowNodeCatalogItem Transform(
        string nodeType,
        string transformId,
        string displayName,
        string description,
        int rank,
        IReadOnlyCollection<WorkflowDataContract> inputContracts,
        WorkflowDataContract outputContract,
        IReadOnlyCollection<string>? requiredConfigurationFields = null)
        => new(
            nodeType,
            WorkflowNodeCategory.Transform,
            rank,
            requiredConfigurationFields ?? [],
            inputContracts,
            outputContract,
            nodeType,
            displayName,
            transformId,
            description);

    private static WorkflowNodeCatalogItem Compliance(
        string nodeType,
        string transformId,
        string displayName,
        string description,
        int rank,
        WorkflowDataContract[] inputContracts,
        WorkflowDataContract outputContract)
        => new(
            nodeType,
            WorkflowNodeCategory.Compliance,
            rank,
            [],
            inputContracts,
            outputContract,
            nodeType,
            displayName,
            transformId,
            description);

    private static WorkflowNodeCatalogItem Destination(string nodeType)
        => new(
            nodeType,
            WorkflowNodeCategory.Destination,
            70,
            [],
            // Whole-resource FHIR destinations additionally accept raw/normalized resources directly (no Mapping
            // node required) — they're spec-owned, so passthrough writes a resource unchanged rather than mapping
            // fields into destination columns. This must stay in step with WorkflowGraphValidator's
            // WholeResourceFhirDestinations set: exempting a destination from the Mapping-node REQUIREMENT while
            // still declaring MappedRecordBatch as its only accepted INPUT rejects the very graph the exemption
            // was meant to allow, since the contract check runs independently of the requirement check. Every
            // other destination type is unaffected: MappedRecordBatch remains their only accepted input, so the
            // Mapping-node requirement still applies to them.
            WholeResourceFhirDestinationNodeTypes.Contains(nodeType)
                ? [WorkflowDataContract.ResourceBatch, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.DeIdentifiedBatch, WorkflowDataContract.MappedRecordBatch]
                : [WorkflowDataContract.MappedRecordBatch],
            WorkflowDataContract.DestinationWriteResult,
            nodeType,
            DisplayNameFor(nodeType),
            TransformIdFor(nodeType),
            DescriptionFor(nodeType));

    private static WorkflowNodeCatalogItem Analytics(string nodeType)
        => new(
            nodeType,
            WorkflowNodeCategory.Analytics,
            90,
            [],
            [WorkflowDataContract.DestinationWriteResult, WorkflowDataContract.MappedRecordBatch],
            WorkflowDataContract.AuditResult,
            nodeType,
            DisplayNameFor(nodeType),
            TransformIdFor(nodeType),
            DescriptionFor(nodeType));

    private static string TransformIdFor(string nodeType)
        => nodeType switch
        {
            WorkflowNodeTypes.EpicSource => "epic",
            // These two must stay identical to the SOURCES catalog ids in the portal's sources.data.ts: the canvas
            // resolves a node's NodeType by matching its vendor id against this transformId (see
            // workflow-graph-mapper.service.ts's catalogForTransform), so a mismatch silently sends the node back
            // to the EpicSourceNode fallback.
            WorkflowNodeTypes.AthenahealthSource => "athena",
            WorkflowNodeTypes.EClinicalWorksSource => "healow",
            WorkflowNodeTypes.SampleSource => "sample",
            WorkflowNodeTypes.SqlServerDestination => "dest-sqlserver",
            WorkflowNodeTypes.CsvDestination => "dest-csv",
            WorkflowNodeTypes.MySqlDestination => "dest-mysql",
            WorkflowNodeTypes.MongoDestination => "dest-mongo",
            WorkflowNodeTypes.PostgreSqlDestination => "dest-postgres",
            WorkflowNodeTypes.MedplumDestination => "dest-medplum",
            WorkflowNodeTypes.FhirRepositoryDestination => "dest-fhir",
            WorkflowNodeTypes.AzureFhirServiceDestination => "dest-azurefhir",
            WorkflowNodeTypes.BlobDestination => "dest-blob",
            WorkflowNodeTypes.DataLakeWebhookDestination => "dest-datalake-webhook",
            WorkflowNodeTypes.DataFabricAzureDestination => "dest-fabric",
            WorkflowNodeTypes.DataFabricWarehouseDestination => "dest-fabric-warehouse",
            WorkflowNodeTypes.AuditLineage => "audit-lineage",
            WorkflowNodeTypes.HedisMeasureReport => "hedis",
            WorkflowNodeTypes.AnomalyDetection => "anomaly",
            WorkflowNodeTypes.PatientAggregation => "patient-agg",
            _ => nodeType
        };

    private static string DisplayNameFor(string nodeType)
        => nodeType switch
        {
            WorkflowNodeTypes.EpicSource => "Epic",
            WorkflowNodeTypes.AthenahealthSource => "athenahealth",
            // eClinicalWorks, not "Healow": Healow is the vendor's patient-app brand, and the portal already shows
            // this vendor as eCW everywhere a user can see it (see source-system-display-names.data.ts).
            WorkflowNodeTypes.EClinicalWorksSource => "eClinicalWorks",
            WorkflowNodeTypes.SampleSource => "Sample FHIR",
            WorkflowNodeTypes.SqlServerDestination => "SQL Server",
            WorkflowNodeTypes.CsvDestination => "CSV",
            WorkflowNodeTypes.MySqlDestination => "MySQL",
            WorkflowNodeTypes.MongoDestination => "MongoDB",
            WorkflowNodeTypes.PostgreSqlDestination => "PostgreSQL",
            WorkflowNodeTypes.MedplumDestination => "Medplum (FHIR)",
            WorkflowNodeTypes.FhirRepositoryDestination => "FHIR Repository (Aidbox)",
            WorkflowNodeTypes.AzureFhirServiceDestination => "Azure FHIR Service",
            WorkflowNodeTypes.BlobDestination => "Azure Blob Storage",
            WorkflowNodeTypes.DataLakeWebhookDestination => "Data Lake Webhook",
            WorkflowNodeTypes.DataFabricAzureDestination => "Microsoft Fabric (OneLake)",
            WorkflowNodeTypes.DataFabricWarehouseDestination => "Microsoft Fabric (Warehouse)",
            WorkflowNodeTypes.AuditLineage => "Audit & Lineage",
            WorkflowNodeTypes.HedisMeasureReport => "HEDIS Measure Report",
            WorkflowNodeTypes.AnomalyDetection => "Anomaly Detection",
            WorkflowNodeTypes.PatientAggregation => "Patient Aggregation",
            _ => nodeType
        };

    private static string DescriptionFor(string nodeType)
        => nodeType switch
        {
            WorkflowNodeTypes.EpicSource => "Read FHIR R4 data from Epic.",
            WorkflowNodeTypes.AthenahealthSource => "Read FHIR R4 data from athenahealth.",
            WorkflowNodeTypes.EClinicalWorksSource => "Read FHIR R4 data from eClinicalWorks.",
            WorkflowNodeTypes.SampleSource => "Use bundled sample FHIR resources.",
            WorkflowNodeTypes.SqlServerDestination => "Write mapped records to Microsoft SQL Server.",
            WorkflowNodeTypes.CsvDestination => "Emit mapped records as CSV files.",
            WorkflowNodeTypes.MySqlDestination => "Write mapped records to MySQL.",
            WorkflowNodeTypes.MongoDestination => "Write mapped records to MongoDB.",
            WorkflowNodeTypes.PostgreSqlDestination => "Write mapped records to PostgreSQL.",
            WorkflowNodeTypes.MedplumDestination => "Write FHIR resources to a Medplum FHIR R4 store (idempotent upsert).",
            WorkflowNodeTypes.FhirRepositoryDestination => "Write FHIR resources to a FHIR repository (e.g. Aidbox).",
            WorkflowNodeTypes.AzureFhirServiceDestination => "Write FHIR resources to Azure Health Data Services (Azure AD client credentials or managed identity).",
            WorkflowNodeTypes.BlobDestination => "Write mapped records to Azure Blob Storage.",
            WorkflowNodeTypes.DataLakeWebhookDestination =>
                "Push mapped records to a data-lake ingestion endpoint over HTTPS (batched NDJSON, signed, retried).",
            WorkflowNodeTypes.DataFabricAzureDestination =>
                "Land mapped records as files in a Microsoft Fabric Lakehouse (OneLake Files; NDJSON, Parquet or CSV).",
            WorkflowNodeTypes.DataFabricWarehouseDestination =>
                "Load mapped records as rows into a Microsoft Fabric Warehouse table (staged Parquet + COPY INTO over TDS).",
            WorkflowNodeTypes.AuditLineage => "Hash-chained audit and record-level lineage.",
            WorkflowNodeTypes.HedisMeasureReport => "Compute HEDIS quality measures.",
            WorkflowNodeTypes.AnomalyDetection => "Flag statistical anomalies.",
            WorkflowNodeTypes.PatientAggregation => "Aggregate a patient-360 view.",
            _ => nodeType
        };
}
