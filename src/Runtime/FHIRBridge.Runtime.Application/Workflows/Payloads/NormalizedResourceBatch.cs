namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

public sealed record NormalizedResourceBatch(IReadOnlyCollection<object> Resources);
