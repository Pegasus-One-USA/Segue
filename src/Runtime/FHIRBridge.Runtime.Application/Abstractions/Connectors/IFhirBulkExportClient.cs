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

    /// <summary>Kicks off the <c>$export</c> job only and returns its status URL (the <c>Content-Location</c>/
    /// <c>Location</c> header). Callers that need to poll on their own schedule — rather than block in
    /// <see cref="ExportAsync"/>'s inline poll loop — use this together with <see cref="PollOnceAsync"/>.</summary>
    Task<string> KickOffExportAsync(
        FhirBulkExportRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary>Performs exactly one GET against <paramref name="statusUrl"/> and maps the response to a
    /// <see cref="BulkExportPollResult"/> instead of looping/sleeping. Callers drive the polling cadence
    /// themselves (e.g. a scheduled Worker tick) rather than blocking for the whole job duration.</summary>
    Task<BulkExportPollResult> PollOnceAsync(
        string statusUrl,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary>Downloads and parses the NDJSON output files from a completed export's manifest.</summary>
    Task<IReadOnlyList<ResourceEnvelope>> DownloadResultsAsync(
        IReadOnlyList<BulkExportFile> files,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
