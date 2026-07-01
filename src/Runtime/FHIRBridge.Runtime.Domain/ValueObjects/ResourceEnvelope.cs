namespace FHIRBridge.Runtime.Domain.ValueObjects;

public sealed record ResourceEnvelope(
    string ResourceType,
    string? ResourceId,
    string RawJson,
    string? VersionId,
    DateTimeOffset? LastUpdated);
