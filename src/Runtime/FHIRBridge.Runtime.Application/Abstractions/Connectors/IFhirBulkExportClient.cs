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

    /// <summary>One GET against <paramref name="statusUrl"/> for OPERATOR DISPLAY, preserving what
    /// <see cref="PollOnceAsync"/> deliberately drops: the <c>X-Progress</c> header and the manifest's
    /// <c>transactionTime</c>/<c>request</c>. Kept separate from <see cref="PollOnceAsync"/> so the poller's own
    /// decision path — which needs none of that — stays untouched by a presentation concern.
    ///
    /// <para>Returns rather than throws on an unexpected status, same as <see cref="PollOnceAsync"/>, so a caller
    /// rendering this in a UI can show the failure instead of surfacing an exception.</para></summary>
    Task<BulkExportStatusSnapshot> GetStatusAsync(
        string statusUrl,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary>Downloads and parses the NDJSON output files from a completed export's manifest.</summary>
    Task<IReadOnlyList<ResourceEnvelope>> DownloadResultsAsync(
        IReadOnlyList<BulkExportFile> files,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary>Downloads and parses the OperationOutcome NDJSON files from a completed export manifest's
    /// <c>error</c> array — the partial-success case where the job succeeded overall but one or more resource
    /// types were excluded (e.g. not supported/authorized for this client).</summary>
    Task<IReadOnlyList<BulkExportPartialFailure>> DownloadPartialFailuresAsync(
        IReadOnlyList<BulkExportFile> errorFiles,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary>Cancels an in-flight (or already-completed) <c>$export</c> job via <c>DELETE</c> on its status URL —
    /// the FHIR Bulk Data spec's cancellation flow. A DELETE against an already-completed export additionally
    /// deletes its generated files on the source server. Idempotent from the caller's perspective: a 404 (already
    /// cancelled/deleted) is treated as success, since the desired end state — nothing left running or downloadable
    /// — already holds.</summary>
    Task CancelExportAsync(
        string statusUrl,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
