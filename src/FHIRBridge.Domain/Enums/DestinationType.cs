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
    DataFabricAzure = 25
}
