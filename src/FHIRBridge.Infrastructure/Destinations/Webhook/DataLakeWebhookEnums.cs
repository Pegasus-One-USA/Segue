namespace FHIRBridge.Infrastructure.Destinations.Webhook;

/// <summary>
/// How a <see cref="Domain.Enums.DestinationType.DataLakeWebhook"/> destination authenticates to its ingestion
/// endpoint. Lake front doors differ far more than storage accounts do — a Fabric Eventstream custom endpoint
/// carries its credential in the URL itself, Splunk HEC wants a bespoke header scheme, Snowpipe Streaming wants an
/// OAuth2 bearer, and a customer-built API Gateway front door usually wants a shared-secret HMAC — so all six are
/// supported rather than assuming one shape.
/// </summary>
public enum DataLakeWebhookAuthMode
{
    /// <summary>The endpoint URL itself is the credential (a pre-authorized Eventstream/Event Grid ingest URL).
    /// The secret still holds that URL when <c>dest_dlwEndpointUrl</c> is left blank, so it never has to be
    /// stored in plaintext next to the destination row.</summary>
    None = 0,
    Bearer,
    ApiKeyHeader,
    Basic,
    /// <summary>Body signed with HMAC-SHA256 over the exact bytes sent, plus a replay-guarding timestamp header.</summary>
    HmacSha256,
    /// <summary>OAuth2 client credentials, acquired and cached through the existing destination token provider.</summary>
    OAuth2ClientCredentials,
}

/// <summary>How a batch of mapped records is framed in the request body.</summary>
public enum DataLakeWebhookPayloadShape
{
    /// <summary>One JSON object per line. The lake-native default: Eventstream, HEC, Snowpipe and every
    /// object-store front door read it without a schema declaration.</summary>
    Ndjson = 0,
    /// <summary>A single JSON array of record objects.</summary>
    JsonArray,
    /// <summary>An object wrapping the array with run/route provenance — <c>{ meta: {...}, records: [...] }</c>.</summary>
    Envelope,
    /// <summary>One request per record. Matches <see cref="Domain.Enums.DestinationType.RestApi"/>'s behavior, for
    /// an endpoint that genuinely cannot accept a batch; every other batching setting is then ignored.</summary>
    RecordPerRequest,
}

/// <summary>What happens when a batch still fails after its retries are exhausted.</summary>
public enum DataLakeWebhookFailureMode
{
    /// <summary>Throw, failing the destination node and the run — the safe default for a clinical feed, since a
    /// silently dropped batch is invisible data loss.</summary>
    Fail = 0,

    /// <summary>Report the batch's records in <see cref="Application.Abstractions.Destinations.DestinationWriteResult.RecordErrors"/>
    /// and keep going with the remaining batches. The run lands as PartialSuccess through the existing
    /// RecordErrors plumbing, so the failure is visible in execution history instead of being swallowed.</summary>
    IsolateBatch,
}

/// <summary>Request body compression.</summary>
public enum DataLakeWebhookCompression
{
    None = 0,
    Gzip,
}
