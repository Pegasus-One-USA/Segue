namespace FHIRBridge.Runtime.Application.Workflows.Catalog;

public static class WorkflowNodeTypes
{
    public const string EpicSource = "EpicSourceNode";
    public const string CernerSource = "CernerSourceNode";
    public const string AthenahealthSource = "AthenahealthSourceNode";
    public const string AllscriptsSource = "AllscriptsSourceNode";
    public const string EClinicalWorksSource = "EClinicalWorksSourceNode";
    public const string MeditechSource = "MeditechSourceNode";
    public const string GenericFhirSource = "GenericFhirSourceNode";
    public const string Hl7v2MllpSource = "Hl7v2MllpSourceNode";
    public const string SampleSource = "SampleSourceNode";
    public const string Normalization = "NormalizationNode";
    public const string DataQualityScoring = "DataQualityScoringNode";
    public const string FlattenExtensions = "FlattenExtensionsNode";
    public const string PatientMatching = "PatientMatchingNode";
    public const string Mapping = "MappingNode";
    public const string RepeatingArrayMapping = "RepeatingArrayMappingNode";
    public const string Terminology = "TerminologyNode";
    public const string TerminologyValidate = "TerminologyValidateNode";
    public const string TerminologyExpand = "TerminologyExpandNode";
    public const string TerminologyTranslate = "TerminologyTranslateNode";
    public const string TerminologyLookup = "TerminologyLookupNode";
    public const string UsCoreValidation = "UsCoreValidationNode";
    public const string Consent = "ConsentNode";
    public const string DeIdentification = "DeIdentificationNode";
    public const string AuditLineage = "AuditLineageNode";
    public const string SqlServerDestination = "SqlServerDestinationNode";
    public const string AzureSqlDestination = "AzureSqlDestinationNode";
    public const string PostgreSqlDestination = "PostgreSqlDestinationNode";
    public const string MySqlDestination = "MySqlDestinationNode";
    public const string SnowflakeDestination = "SnowflakeDestinationNode";
    public const string BlobDestination = "BlobDestinationNode";
    public const string S3Destination = "S3DestinationNode";
    public const string FhirRepositoryDestination = "FhirRepositoryDestinationNode";
    public const string CsvDestination = "CsvDestinationNode";
    public const string ExcelDestination = "ExcelDestinationNode";
    public const string NdjsonDestination = "NdjsonDestinationNode";
    public const string ParquetDestination = "ParquetDestinationNode";
    public const string AvroDestination = "AvroDestinationNode";
    public const string ProtobufDestination = "ProtobufDestinationNode";
    public const string PdfDestination = "PdfDestinationNode";
    public const string SftpDestination = "SftpDestinationNode";
    public const string RestApiDestination = "RestApiDestinationNode";
    public const string InMemoryDestination = "InMemoryDestinationNode";
    public const string PowerBiDestination = "PowerBiDestinationNode";
    public const string TableauDestination = "TableauDestinationNode";
    public const string DatabricksDestination = "DatabricksDestinationNode";
    public const string MongoDestination = "MongoDestinationNode";
    public const string MedplumDestination = "MedplumDestinationNode";
    /// <summary>Phase 2 example: a Destination-category node whose input is a previous destination's write result,
    /// not fresh mapped records — demonstrates chaining a destination into another node via a bespoke catalog rank
    /// tier (71, above Destination's 70) rather than relaxing the graph validator. See
    /// docs/backend/05-workflow-node-checkpoints-plan.md §4.2.</summary>
    public const string WebhookNotifier = "WebhookNotifierNode";
    public const string HedisMeasureReport = "HedisMeasureReportNode";
    public const string AnomalyDetection = "AnomalyDetectionNode";
    public const string PatientAggregation = "PatientAggregationNode";
}
