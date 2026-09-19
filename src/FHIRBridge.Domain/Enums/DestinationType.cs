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
    /// Microsoft Fabric, FILE landing surfaces — today OneLake's Files area (Entra-only auth over the OneLake blob
    /// endpoint, NDJSON/CSV/Parquet). Landing surface is still selected per destination by <c>dest_fabricMode</c>,
    /// because Files and Eventstream remain distinct protocols under this one type. Writing into a Lakehouse
    /// <c>Tables/</c> path is rejected on purpose: a registered Fabric table requires the Delta transaction log,
    /// which this writer does not produce. See <c>FabricDestinationSettings</c>.
    ///
    /// <para>Deliberately NOT the Warehouse surface — see <see cref="DataFabricWarehouse"/> for why that is its own
    /// type rather than one more mode here.</para>
    /// </summary>
    DataFabricAzure = 25,

    /// <summary>
    /// Microsoft Fabric WAREHOUSE — rows into a Warehouse table via staged Parquet plus <c>COPY INTO</c> over the
    /// TDS endpoint (<c>WarehouseTableLandingStrategy</c>).
    ///
    /// <para><b>Why this is its own type rather than a mode of <see cref="DataFabricAzure"/>.</b> It answers
    /// differently to every question the system asks a destination: it speaks TDS rather than the OneLake blob
    /// endpoint, it has a live queryable schema (so the mapping UI can offer a real table/column picker and
    /// ALTER/CREATE TABLE authoring, see <c>SqlDestinationSchemaService</c>), and field mapping applies to it at
    /// all (see the portal's SQL_FAMILY_DESTINATION_TYPES). Modelling it as a mode meant every one of those gates
    /// had to re-parse <c>dest_fabricMode</c> out of connection metadata to answer a question the type itself
    /// should answer — a tax paid at a dozen call sites, each one silently mis-routing a destination when missed.
    /// Same reasoning FabricLandingMode itself documents for not inferring the surface from a URL.</para>
    ///
    /// <para>Shares <c>MappedDataFabricDestinationWriter</c> with <see cref="DataFabricAzure"/>: that writer already
    /// resolves an <c>IFabricLandingStrategy</c> from the configured mode and delegates, so the split is a
    /// classification change, not a second implementation of the same protocol.</para>
    /// </summary>
    DataFabricWarehouse = 26
}
