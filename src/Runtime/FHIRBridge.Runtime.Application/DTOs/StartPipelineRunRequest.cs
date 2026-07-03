namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record StartPipelineRunRequest(
    FhirSourceConfiguration Source,
    RuntimeDestinationConfiguration Destination,
    IReadOnlyCollection<string> ResourceTypes,
    string? TriggeredBy,
    string? CorrelationId);
