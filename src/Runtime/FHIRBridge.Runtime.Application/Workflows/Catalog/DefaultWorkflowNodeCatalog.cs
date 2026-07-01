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
        Compliance(WorkflowNodeTypes.Consent, 10, WorkflowDataContract.ResourceBatch, WorkflowDataContract.ResourceBatch),
        Compliance(WorkflowNodeTypes.UsCoreValidation, 20, WorkflowDataContract.ResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.Normalization, 30, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.FlattenExtensions, 31, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.DataQualityScoring, 32, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.PatientMatching, 33, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.Terminology, 40, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyValidate, 41, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyLookup, 42, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyTranslate, 43, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Transform(WorkflowNodeTypes.TerminologyExpand, 44, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.NormalizedResourceBatch),
        Compliance(WorkflowNodeTypes.DeIdentification, 50, WorkflowDataContract.NormalizedResourceBatch, WorkflowDataContract.DeIdentifiedBatch),
        Transform(WorkflowNodeTypes.Mapping, 60, WorkflowDataContract.DeIdentifiedBatch, WorkflowDataContract.MappedRecordBatch),
        Transform(WorkflowNodeTypes.RepeatingArrayMapping, 61, WorkflowDataContract.MappedRecordBatch, WorkflowDataContract.MappedRecordBatch),
        Destination(WorkflowNodeTypes.SqlServerDestination),
        Destination(WorkflowNodeTypes.CsvDestination),
        // GATED (SQL/CSV phase): only SqlServer + CSV destinations are exposed in the palette. The writers below
        // remain registered in ConfiguredDestinationWriterFactory and can be re-listed here as each is productized.
        // Destination(WorkflowNodeTypes.AzureSqlDestination),
        // Destination(WorkflowNodeTypes.PostgreSqlDestination),
        // Destination(WorkflowNodeTypes.MySqlDestination),
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
            WorkflowNodeTypes.AuditLineage),
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
            nodeType);

    private static WorkflowNodeCatalogItem Transform(
        string nodeType,
        int rank,
        WorkflowDataContract inputContract,
        WorkflowDataContract outputContract,
        IReadOnlyCollection<string>? requiredConfigurationFields = null)
        => new(
            nodeType,
            WorkflowNodeCategory.Transform,
            rank,
            requiredConfigurationFields ?? [],
            [inputContract],
            outputContract,
            nodeType);

    private static WorkflowNodeCatalogItem Compliance(
        string nodeType,
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
            nodeType);

    private static WorkflowNodeCatalogItem Destination(string nodeType)
        => new(
            nodeType,
            WorkflowNodeCategory.Destination,
            70,
            [],
            [WorkflowDataContract.MappedRecordBatch],
            WorkflowDataContract.DestinationWriteResult,
            nodeType);

    private static WorkflowNodeCatalogItem Analytics(string nodeType)
        => new(
            nodeType,
            WorkflowNodeCategory.Analytics,
            90,
            [],
            [WorkflowDataContract.DestinationWriteResult, WorkflowDataContract.MappedRecordBatch],
            WorkflowDataContract.AuditResult,
            nodeType);
}
