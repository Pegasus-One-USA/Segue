namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

public sealed record ResourceBatch(IReadOnlyCollection<ResourceEnvelope> Resources);

public sealed record ResourceEnvelope(string ResourceType, string ResourceId, object Payload);
