using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <summary>
/// Delivers one already-serialized batch body to an API Endpoint destination, applying that destination's auth,
/// signing, compression and retry policy. The seam that lets <see cref="MappedApiEndpointDestinationWriter"/> be
/// unit-tested without a live HTTP endpoint — the writer owns batching and payload framing; this owns the wire.
/// </summary>
public interface IApiEndpointSender
{
    Task<ApiEndpointSendResult> SendAsync(
        DestinationConfiguration destination,
        ApiEndpointSettings settings,
        ApiEndpointBatch batch,
        CancellationToken cancellationToken);
}

/// <summary>One request's worth of records, already framed into <paramref name="Body"/> by the writer.</summary>
/// <param name="Body">The exact UTF-8 text to send (before optional gzip). HMAC signs these bytes.</param>
/// <param name="RecordCount">Records represented by this body, for the batch's provenance headers.</param>
/// <param name="IdempotencyKey">Stable across retries and across whole re-runs of the same batch of the same
/// pipeline run, so an endpoint that honors it de-duplicates rather than double-landing records.</param>
/// <param name="ResourceType">FHIR resource type of this batch, surfaced as a routing header.</param>
public sealed record ApiEndpointBatch(
    string Body,
    int RecordCount,
    string IdempotencyKey,
    string ResourceType,
    string DestinationObject);

/// <param name="Delivered">True when a response in the accepted set came back within the retry budget.</param>
/// <param name="StatusCode">Last HTTP status observed, or null when every attempt failed before a response.</param>
/// <param name="Attempts">Attempts actually made (1 when it succeeded first try).</param>
/// <param name="Error">Why it ultimately failed, safe to log and surface in execution history. Carries the status
/// code and a truncated response reason only — never the request body, which may hold PHI.</param>
public sealed record ApiEndpointSendResult(
    bool Delivered,
    int? StatusCode,
    int Attempts,
    string? Error);
