using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Catalog;

public sealed class DefaultWorkflowNodeCatalog : IWorkflowNodeCatalog
{
    private static readonly WorkflowNodeCatalogItem[] Items =
    [
        Source(WorkflowNodeTypes.EpicSource),
        // GATED (SQL/CSV phase): only Epic + Sample sources are exposed in the palette. Re-enable the other vendors
        // (they reuse the Epic search client) once the generic Source hierarchy + ApplicationType axis land.
        // Source(WorkflowNodeTypes.CernerSource),
        // Source(WorkflowNodeTypes.AthenahealthSource),
        // Source(WorkflowNodeTypes.AllscriptsSource),
        // Source(WorkflowNodeTypes.EClinicalWorksSource),
        // Source(WorkflowNodeTypes.MeditechSource),
        // Source(WorkflowNodeTypes.GenericFhirSource),
        // Source(WorkflowNodeTypes.Hl7v2MllpSource),
        Source(WorkflowNodeTypes.SampleSource),
        Compliance(WorkflowNodeTypes.Consent, "consent", "Consent", "Apply configured consent policy.", 10, WorkflowDataContract.ResourceBatch, WorkflowDataContract.ResourceBatch),
        Compliance(WorkflowNodeTypes.UsCoreValidation, "fhir-validation", "FHIR Validation", "Validate resources against US Core / base R4 profiles.", 20, WorkflowDataContract.ResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.Normalization, "normalize", "Normalize Data", "Normalize FHIR resources for downstream transforms.", 30, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.FlattenExtensions, "flatten-extensions", "Flatten Extensions", "Flatten FHIR extensions into mappable fields.", 31, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.DataQualityScoring, "data-quality", "Data Quality Scoring", "Score resource completeness and data quality.", 32, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.PatientMatching, "patient-matching", "Patient Matching (MPI)", "Match patients against a master patient index.", 33, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.Terminology, "terminology", "Terminology Mapping", "Validate / translate ICD, SNOMED, LOINC, RxNorm codes.", 40, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyValidate, "terminology-validate", "Terminology Validate", "Validate coded values against configured terminology services.", 41, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyLookup, "terminology-lookup", "Terminology Lookup", "Lookup display and metadata for coded values.", 42, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyTranslate, "terminology-translate", "Terminology Translate", "Translate coded values between code systems.", 43, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyExpand, "terminology-expand", "Terminology Expand", "Expand value sets for downstream validation.", 44, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Compliance(WorkflowNodeTypes.DeIdentification, "deid-safeharbor", "De-identification", "Apply HIPAA de-identification policy when required.", 50, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.DeIdentifiedBatch),
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
        // GATED (SQL/CSV phase): only SqlServer + CSV + MySql + Mongo + PostgreSql destinations are exposed in the palette.
        // The writers below remain registered in ConfiguredDestinationWriterFactory and can be re-listed here as each is productized.
        // Destination(WorkflowNodeTypes.AzureSqlDestination),
        // Destination(WorkflowNodeTypes.SnowflakeDestination),
        // Destination(WorkflowNodeTypes.PowerBiDestination),
        // Destination(WorkflowNodeTypes.TableauDestination),
        // Destination(WorkflowNodeTypes.DatabricksDestination),
        // Destination(WorkflowNodeTypes.BlobDestination),
        // Destination(WorkflowNodeTypes.S3Destination),
        // Destination(WorkflowNodeTypes.FhirRepositoryDestination),
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
        WorkflowDataContract inputContract,
        WorkflowDataContract outputContract)
        => new(
            nodeType,
            WorkflowNodeCategory.Compliance,
            rank,
            [],
            [inputContract],
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
            [WorkflowDataContract.MappedRecordBatch],
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
            WorkflowNodeTypes.SampleSource => "sample",
            WorkflowNodeTypes.SqlServerDestination => "dest-sqlserver",
            WorkflowNodeTypes.CsvDestination => "dest-csv",
            WorkflowNodeTypes.MySqlDestination => "dest-mysql",
            WorkflowNodeTypes.MongoDestination => "dest-mongo",
            WorkflowNodeTypes.PostgreSqlDestination => "dest-postgres",
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
            WorkflowNodeTypes.SampleSource => "Sample FHIR",
            WorkflowNodeTypes.SqlServerDestination => "SQL Server",
            WorkflowNodeTypes.CsvDestination => "CSV",
            WorkflowNodeTypes.MySqlDestination => "MySQL",
            WorkflowNodeTypes.MongoDestination => "MongoDB",
            WorkflowNodeTypes.PostgreSqlDestination => "PostgreSQL",
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
            WorkflowNodeTypes.SampleSource => "Use bundled sample FHIR resources.",
            WorkflowNodeTypes.SqlServerDestination => "Write mapped records to Microsoft SQL Server.",
            WorkflowNodeTypes.CsvDestination => "Emit mapped records as CSV files.",
            WorkflowNodeTypes.MySqlDestination => "Write mapped records to MySQL.",
            WorkflowNodeTypes.MongoDestination => "Write mapped records to MongoDB.",
            WorkflowNodeTypes.PostgreSqlDestination => "Write mapped records to PostgreSQL.",
            WorkflowNodeTypes.AuditLineage => "Hash-chained audit and record-level lineage.",
            WorkflowNodeTypes.HedisMeasureReport => "Compute HEDIS quality measures.",
            WorkflowNodeTypes.AnomalyDetection => "Flag statistical anomalies.",
            WorkflowNodeTypes.PatientAggregation => "Aggregate a patient-360 view.",
            _ => nodeType
        };
}
