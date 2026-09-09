using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Webhook;

/// <summary>
/// Delivers one already-serialized batch body to a data-lake webhook endpoint, applying that destination's auth,
/// signing, compression and retry policy. The seam that lets <see cref="MappedDataLakeWebhookDestinationWriter"/>
/// be unit-tested without a live HTTP endpoint — the same role <see cref="Blob.IBlobContainerClientFactory"/> plays
/// for the blob writer. The writer owns batching and payload framing; this owns the wire.
/// </summary>
public interface IDataLakeWebhookSender
{
    Task<DataLakeWebhookSendResult> SendAsync(
        DestinationConfiguration destination,
        DataLakeWebhookSettings settings,
        DataLakeWebhookBatch batch,
        CancellationToken cancellationToken);
}

/// <summary>
/// One request's worth of records, already framed into <paramref name="Body"/> by the writer.
/// </summary>
/// <param name="Body">The exact UTF-8 text to send (before optional gzip). HMAC signs these bytes.</param>
/// <param name="RecordCount">Records represented by this body, for the batch's provenance headers.</param>
/// <param name="IdempotencyKey">
/// Stable across retries AND across whole re-runs of the same batch of the same pipeline run, so a lake front door
/// that honors it (Eventstream, HEC, most API-Gateway ingest patterns) de-duplicates rather than double-landing
/// records when a run is retried. Derived from pipeline run + destination object + batch index — never a fresh Guid,
/// which would defeat the entire point.
/// </param>
/// <param name="ResourceType">FHIR resource type of this batch, surfaced as a routing header.</param>
public sealed record DataLakeWebhookBatch(
    string Body,
    int RecordCount,
    string IdempotencyKey,
    string ResourceType,
    string DestinationObject);

/// <param name="Delivered">True when a response in the accepted set came back within the retry budget.</param>
/// <param name="StatusCode">Last HTTP status observed, or null when every attempt failed before a response.</param>
/// <param name="Attempts">Attempts actually made (1 when it succeeded first try).</param>
/// <param name="Error">
/// Why it ultimately failed, safe to log and to surface in execution history. Deliberately carries the status code
/// and a truncated response reason only — never the request body, which holds PHI.
/// </param>
public sealed record DataLakeWebhookSendResult(
    bool Delivered,
    int? StatusCode,
    int Attempts,
    string? Error);
