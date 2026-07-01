namespace FHIRBridge.Runtime.Application.Workflows;

public enum WorkflowDataContract
{
    None,
    ResourceBatch,
    NormalizedResourceBatch,
    MappedRecordBatch,
    DeIdentifiedBatch,
    DestinationWriteResult,
    AuditResult
}
