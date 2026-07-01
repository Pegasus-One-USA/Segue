namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record StartPipelineRunRequest(
    Guid TenantId,
    FhirSourceConfiguration Source,
    RuntimeDestinationConfiguration Destination,
    IReadOnlyCollection<string> ResourceTypes,
    string? TriggeredBy,
    string? CorrelationId);
