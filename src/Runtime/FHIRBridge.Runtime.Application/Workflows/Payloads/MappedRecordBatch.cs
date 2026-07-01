namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

public sealed record MappedRecordBatch(IReadOnlyCollection<object> Records);
