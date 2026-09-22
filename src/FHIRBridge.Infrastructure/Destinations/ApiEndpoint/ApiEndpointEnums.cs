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

/// <summary>
/// Whether this destination combines MORE THAN ONE mapped resource type's records into a single outgoing
/// document — every other setting on this destination (auth, payload shape, the single-resource Request Body
/// Template, batching/retry) behaves exactly as it always has when this is <see cref="None"/>, which is every
/// destination that existed before this setting did. See <see cref="ApiEndpointResourceRelation"/> for how
/// each participating resource type is declared, and MappedApiEndpointDestinationWriter's own remarks for why
/// this is handled entirely inside the writer (an in-memory per-run accumulator), never by changing how or how
/// often the pipeline orchestrator calls WriteAsync — every other destination type, and every ApiEndpoint
/// destination that doesn't opt into this, is completely unaffected.
/// </summary>
public enum ApiEndpointMultiResourceMode
{
    /// <summary>Default — one resource type per write, exactly today's behavior.</summary>
    None = 0,
    /// <summary>Every participating resource type's records land as its own sibling array in one combined
    /// document (<c>{ "patients": [...], "encounters": [...] }</c>) — no correlation between them.</summary>
    Flat,
    /// <summary>A child resource type's records are correlated to their parent (by a mapped reference field)
    /// and nested inside that parent's own record (<c>{ "patients": [{ ..., "encounters": [...] }] }</c>).</summary>
    Nested,
}
