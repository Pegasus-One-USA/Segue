namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <summary>
/// How an <see cref="Domain.Enums.DestinationType.ApiEndpoint"/> destination authenticates. Deliberately the
/// widest auth surface of any destination writer — this type exists specifically so an arbitrary customer- or
/// partner-owned API never forces a workaround: shared-secret HMAC, a static API key (header or query string),
/// HTTP Basic, a static bearer token, OAuth2 client credentials, or a client certificate for mutual TLS.
/// </summary>
public enum ApiEndpointAuthMode
{
    /// <summary>No credential attached at all — an already-public or network-restricted endpoint.</summary>
    None = 0,
    Bearer,
    ApiKeyHeader,
    /// <summary>The API key travels in the query string (e.g. <c>?api_key=...</c>) rather than a header — the
    /// shape a number of public/partner APIs still require.</summary>
    ApiKeyQuery,
    Basic,
    /// <summary>Body signed with HMAC-SHA256 over the exact bytes sent, plus a replay-guarding timestamp header —
    /// same convention as <see cref="Webhook.DataLakeWebhookAuthMode.HmacSha256"/>.</summary>
    HmacSha256,
    /// <summary>OAuth2 client credentials, acquired and cached through the existing destination token provider.</summary>
    OAuth2ClientCredentials,
    /// <summary>Mutual TLS: a client certificate (PKCS#12/PFX) presented during the TLS handshake instead of any
    /// header-based credential. The secret holds the base64 PFX, optionally suffixed with
    /// <c>|&lt;password&gt;</c> when the PFX itself is password-protected.</summary>
    ClientCertificate,
}

/// <summary>How a batch of mapped records is framed in the request body.</summary>
public enum ApiEndpointPayloadShape
{
    /// <summary>A single JSON array of record objects — the most broadly accepted shape for a generic API.</summary>
    JsonArray = 0,
    /// <summary>One JSON object per line (NDJSON).</summary>
    Ndjson,
    /// <summary>An object wrapping the array with run/route provenance — <c>{ meta: {...}, records: [...] }</c>.</summary>
    Envelope,
    /// <summary>One request per record — for an endpoint that cannot accept a batch at all. Every other batching
    /// setting is then ignored, matching <see cref="Domain.Enums.DestinationType.RestApi"/>'s behavior.</summary>
    RecordPerRequest,
}

/// <summary>What happens when a batch still fails after its retries are exhausted.</summary>
public enum ApiEndpointFailureMode
{
    /// <summary>Throw, failing the destination node and the run — the safe default: a silently dropped batch is
    /// invisible data loss.</summary>
    Fail = 0,

    /// <summary>Report the batch's records in <see cref="Application.Abstractions.Destinations.DestinationWriteResult.RecordErrors"/>
    /// and keep going with the remaining batches (the run lands as PartialSuccess).</summary>
    IsolateBatch,
}

/// <summary>Request body compression.</summary>
public enum ApiEndpointCompression
{
    None = 0,
    Gzip,
}
