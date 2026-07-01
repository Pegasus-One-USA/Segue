namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

public sealed record DeIdentifiedBatch(IReadOnlyCollection<object> Records);
