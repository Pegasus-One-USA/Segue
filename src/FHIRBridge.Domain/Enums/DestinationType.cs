namespace FHIRBridge.Domain.Enums;

public enum DestinationType
{
    InMemory = 0,
    SqlServer = 1,
    AzureSql = 2,
    RestApi = 3,
    BlobStorage = 4,
    Csv = 5,
    Excel = 6,
    PowerBi = 7,
    Snowflake = 8,
    FhirRepository = 9,
    PostgreSql = 10,
    MySql = 11,
    S3 = 12,
    Ndjson = 13,
    Parquet = 14,
    Sftp = 15,
    Tableau = 16,
    Pdf = 17,
    Avro = 18,
    Protobuf = 19,
    Databricks = 20,
    Mongo = 21,
    Medplum = 22,
    AzureFhirService = 23,

    /// <summary>
    /// Data-plane HTTP delivery of mapped records to a data-lake ingestion endpoint — a Fabric Eventstream custom
    /// endpoint, Databricks/Snowpipe Streaming REST ingest, an API-Gateway/Lambda front door over S3, an Event Grid
    /// or Splunk HEC collector. Deliberately distinct from two neighbours it is often confused with:
    /// <see cref="RestApi"/> POSTs one record per request with no auth beyond the raw secret, no batching and no
    /// retry; and the Runtime plane's <c>WebhookNotifierNode</c> is a CONTROL-plane ping that carries a write
    /// summary only and refuses record-level data outright. This type carries PHI, so it batches, signs, retries
    /// and is HTTPS-only. See <c>DataLakeWebhookSettings</c>.
    /// </summary>
    DataLakeWebhook = 24,

    /// <summary>
    /// Microsoft Fabric. Landing surface is selected per destination by <c>dest_fabricMode</c> — today OneLake's
    /// Files area (Entra-only auth over the OneLake blob endpoint, NDJSON/CSV/Parquet); Warehouse COPY INTO and
    /// Eventstream are declared but not yet implemented and fail fast rather than silently mis-writing. Writing
    /// into a Lakehouse <c>Tables/</c> path is rejected on purpose: a registered Fabric table requires the Delta
    /// transaction log, which this writer does not produce. See <c>FabricDestinationSettings</c>.
    /// </summary>
    DataFabricAzure = 25,

    /// <summary>
    /// General-purpose, fully configurable outbound REST API — the "bring your own endpoint" destination. Unlike
    /// <see cref="RestApi"/> (one record per request, no auth beyond a raw secret, no batching, no retry), this
    /// type exposes every option a real integration needs: HTTP method, seven auth modes (none/basic/bearer/API
    /// key header or query/HMAC-SHA256/OAuth2 client credentials/client certificate mTLS), custom headers and
    /// query parameters, batching with byte and count caps, gzip, retry with backoff, and a configurable
    /// success/failure contract. Also distinct from <see cref="DataLakeWebhook"/>, which is purpose-built for
    /// data-lake ingestion front doors (Fabric/Databricks/HEC) and always PHI-carrying/https-only; this type is
    /// the general destination for an arbitrary customer- or partner-owned HTTP API. See <c>ApiEndpointSettings</c>.
    /// </summary>
    ApiEndpoint = 26
}
