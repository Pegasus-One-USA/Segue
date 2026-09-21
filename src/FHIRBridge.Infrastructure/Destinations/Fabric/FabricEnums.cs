namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Which Fabric surface a <see cref="Domain.Enums.DestinationType.DataFabricAzure"/> destination lands in.
/// "Fabric as a destination" is not one target — it is at least three, with different protocols, different auth
/// audiences and different failure modes — so the surface is an explicit per-destination choice rather than
/// something inferred from a URL.
/// </summary>
public enum FabricLandingMode
{
    /// <summary>
    /// Files into a Lakehouse's <c>Files/</c> (unmanaged) area over the OneLake endpoint. Implemented. The
    /// customer then exposes them as a table with a shortcut, a notebook, or a Fabric pipeline — see
    /// <see cref="FabricDestinationSettings"/> on why the <c>Tables/</c> area is refused outright.
    /// </summary>
    OneLakeFiles = 0,

    /// <summary>
    /// Rows into a Fabric Warehouse table, via staged Parquet in a Lakehouse plus <c>COPY INTO</c> over the TDS
    /// endpoint. Note that a Lakehouse's SQL analytics endpoint is read-only, so this mode means a real Warehouse
    /// item, not that endpoint — which is also why <see cref="LakehouseTable"/> cannot be served the same way.
    /// </summary>
    WarehouseTable = 1,

    /// <summary>
    /// Events into a Fabric Eventstream. Declared but NOT implemented here on purpose: an Eventstream custom
    /// endpoint is plain authenticated HTTP, so it is already fully served by the
    /// <see cref="Domain.Enums.DestinationType.DataLakeWebhook"/> destination — pointing a customer at that is
    /// better than a second, thinner implementation of the same wire protocol.
    /// </summary>
    Eventstream = 2,

    /// <summary>
    /// Rows into a Lakehouse <c>Tables/</c> Delta table. Distinct from <see cref="OneLakeFiles"/> in one decisive
    /// way: a registered Fabric table is a Delta table, defined by the <c>_delta_log</c> transaction log beside
    /// the Parquet, so the data files alone are not a table. This mode writes that log; OneLakeFiles refuses a
    /// <c>Tables/</c> path precisely because it does not.
    /// </summary>
    LakehouseTable = 3,
}

/// <summary>Serialization format for files landed in OneLake.</summary>
public enum FabricFileFormat
{
    /// <summary>Line-delimited JSON. Schema-flexible, so a mapping change does not break existing files.</summary>
    Ndjson = 0,

    /// <summary>Columnar. What a Spark/Warehouse consumer actually wants for analytics volume.</summary>
    Parquet = 1,

    /// <summary>Header-and-rows text, for a consumer that only reads CSV.</summary>
    Csv = 2,
}

/// <summary>
/// How OneLake authenticates. Entra only — and that is a Fabric constraint, not a simplification: OneLake does not
/// accept storage account keys or SAS tokens, so the five-mode shape
/// <see cref="Blob.BlobDestinationAuthMode"/> offers for Blob Storage cannot be reused here.
/// </summary>
public enum FabricAuthMode
{
    /// <summary>The host's own managed identity (or local developer credential chain).</summary>
    ManagedIdentity = 0,

    /// <summary>An Entra app registration: tenant id + client id in metadata, client secret in Key Vault.</summary>
    ServicePrincipal = 1,
}

/// <summary>Folder partitioning applied under the configured base path.</summary>
public enum FabricPartitionScheme
{
    /// <summary>Everything in the base path.</summary>
    None = 0,

    /// <summary>Hive-style <c>resourceType=Patient/</c>.</summary>
    ResourceType = 1,

    /// <summary>Hive-style <c>ingest_date=2026-09-08/</c>.</summary>
    IngestDate = 2,

    /// <summary>Both, resource type outermost — the layout a Spark reader partition-prunes best.</summary>
    ResourceTypeAndIngestDate = 3,
}

/// <summary>How rows are applied to a Fabric table destination (Warehouse or Lakehouse Delta).</summary>
public enum FabricTableWriteMode
{
    /// <summary>Every batch appends. No key needed, and duplicates are the caller's problem downstream.</summary>
    Append = 0,

    /// <summary>
    /// Match on the mapping field flagged <c>IsUpsertKey</c> (falling back to <c>SourceResourceId</c>, the same
    /// convention the SQL Server writer uses) and update in place, else insert. The reason a table surface can
    /// offer this at all and <see cref="FabricLandingMode.OneLakeFiles"/> cannot: a file drop has nothing to
    /// match against.
    /// </summary>
    Upsert = 1,
}
