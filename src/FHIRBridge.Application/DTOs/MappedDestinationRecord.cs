namespace FHIRBridge.Application.DTOs;

public sealed record MappedDestinationRecord(
    Guid PipelineRunId,
    string ResourceType,
    string DestinationObject,
    string? SourceResourceId,
    IReadOnlyDictionary<string, object?> Values,
    /// <summary>
    /// The normalized source FHIR resource JSON, carried through so FHIR-native destinations (e.g. a FHIR
    /// repository) can persist a valid resource rather than the flattened tabular <see cref="Values"/>.
    /// Null when the originating flow has no FHIR document (kept optional for backward compatibility).
    /// </summary>
    string? SourceJson = null);
