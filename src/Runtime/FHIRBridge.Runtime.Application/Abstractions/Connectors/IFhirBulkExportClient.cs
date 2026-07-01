using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

/// <summary>
/// Runs the FHIR Bulk Data <c>$export</c> flow against a source server: kicks off the async job, polls the status
/// URL until complete, then downloads and parses the NDJSON output into resources for the pipeline.
/// </summary>
public interface IFhirBulkExportClient
{
    Task<IReadOnlyList<ResourceEnvelope>> ExportAsync(
        FhirBulkExportRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
