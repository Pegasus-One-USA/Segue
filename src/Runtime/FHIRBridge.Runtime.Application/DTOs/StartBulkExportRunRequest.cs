namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>Triggers a pipeline run sourced from a FHIR Bulk Data <c>$export</c> job.</summary>
public sealed record StartBulkExportRunRequest(
    FhirSourceConfiguration Source,
    RuntimeDestinationConfiguration Destination,
    FhirBulkExportRequest Export,
    string? TriggeredBy,
    string? CorrelationId);
