using System.Text.RegularExpressions;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Parsed, non-secret configuration for a <see cref="Domain.Enums.DestinationType.DataFabricAzure"/> destination,
/// read from the same flat <c>dest_*</c>-keyed <see cref="DestinationConfiguration.ConnectionMetadataJson"/> bag
/// every other writer uses — no schema migration to add this destination type, exactly as
/// <see cref="Blob.BlobDestinationSettings"/> established. The client secret (service-principal mode) lives only
/// in <see cref="DestinationConfiguration.SecretReference"/>'s Key Vault entry.
///
/// OneLake addressing, which the rest of this class exists to build: a workspace behaves as the storage
/// *container* and an item as the first path segment, so
/// <c>https://onelake.blob.fabric.microsoft.com/{workspace}/{item}.Lakehouse/Files/{path}</c> is reachable with the
/// ordinary blob SDK against an Entra token. That is what lets this destination reuse the blob client machinery
/// instead of carrying a second storage stack.
/// </summary>
public sealed record FabricDestinationSettings(
    FabricLandingMode Mode,
    FabricAuthMode AuthMode,
    string Workspace,
    string ItemName,
    string ItemType,
    string BasePath,
    FabricFileFormat FileFormat,
    FabricPartitionScheme Partitioning,
    string? TenantId,
    string? ClientId,
    string? ManagedIdentityClientId,
    string? AuthorityHost,
    string EndpointSuffix,
    string? AccountUrlOverride,

    // ---- Warehouse landing (FabricLandingMode.WarehouseTable) only; null/default otherwise ----

    /// <summary>
    /// The Warehouse's SQL connection string (TDS). A separate endpoint from <see cref="AccountUrl"/> — OneLake
    /// and the Warehouse are different services — so it is configured rather than derived. No credentials belong
    /// in it: the same Entra identity that reaches OneLake is attached as an access token at connection time.
    /// </summary>
    string? WarehouseSqlEndpoint = null,

    /// <summary>Target schema. Defaults to <c>dbo</c>, as in a Warehouse created through the Fabric UI.</summary>
    string WarehouseSchema = "dbo",

    /// <summary>
    /// Target table. Defaults to the mapping profile's destination object when blank, so one destination can
    /// serve many resource types — the same defaulting the relational writers do.
    /// </summary>
    string? WarehouseTable = null,

    /// <summary>Append (default) or Upsert. See <see cref="FabricTableWriteMode"/>.</summary>
    FabricTableWriteMode WarehouseWriteMode = FabricTableWriteMode.Append,

    /// <summary>
    /// Lakehouse that holds the staging Parquet a COPY INTO reads from. A Warehouse has no Files area of its own,
    /// so a load must stage somewhere addressable by both — which in Fabric means a Lakehouse in the same
    /// workspace. Required for Warehouse mode; there is no sensible default, because guessing a Lakehouse name
    /// would fail at load time rather than at save time.
    /// </summary>
    string? WarehouseStagingLakehouse = null,

    /// <summary>Folder under the staging Lakehouse's Files area. Cleaned up after each load.</summary>
    string WarehouseStagingPath = "_staging",

    /// <summary>
    /// Whether COPY INTO should impersonate the Fabric WORKSPACE IDENTITY when reading the staged Parquet,
    /// rather than reading it as the executing (connection) identity.
    ///
    /// <para>Default false, which matches the documented default: "The executing user's Microsoft Entra identity
    /// is the default credential for source access. No credential needs to be specified." That works when the
    /// connecting identity itself holds Contributor on the workspace holding the staging Lakehouse.</para>
    ///
    /// <para>Set true when it does not — the workspace identity then authorizes the source read instead, and the
    /// executing identity needs no direct permission on the staged file. Requires a provisioned workspace
    /// identity, and the executing identity must hold at least the Viewer workspace role to impersonate it
    /// (item permissions alone are not enough).</para>
    /// </summary>
    bool WarehouseUseWorkspaceIdentity = false,

    // ---- Lakehouse Delta landing (FabricLandingMode.LakehouseTable) only; default otherwise ----

    /// <summary>
    /// Schema name for a SCHEMA-ENABLED Lakehouse, whose tables live at <c>Tables/{schema}/{table}</c> rather than
    /// <c>Tables/{table}</c>.
    ///
    /// <para>Null (the default) means a classic Lakehouse, and is deliberately not defaulted to <c>dbo</c>: the
    /// two layouts are not interchangeable, and writing to the wrong one produces a folder Fabric never registers
    /// as a table rather than an error. A customer whose Lakehouse is schema-enabled sets this explicitly.</para>
    /// </summary>
    string? LakehouseSchema = null)
{
    /// <summary>
    /// OneLake path prefix of the staging Lakehouse's Files area, e.g. <c>Stage.Lakehouse/Files/_staging</c>.
    /// Warehouse mode only.
    /// </summary>
    /// <remarks>
    /// A GUID is emitted bare, without the <c>.Lakehouse</c> suffix, because OneLake addresses an item either by
    /// friendly name (<c>Stage.Lakehouse</c>) or by item id — never by id with a type suffix. This matters on a
    /// tenant with friendly-name support disabled, where the DFS endpoint rejects names outright with
    /// <c>FriendlyNameSupportDisabled</c>; see <see cref="IsItemId"/>.
    /// </remarks>
    public string WarehouseStagingRootPath =>
        $"{FormatItemSegment(WarehouseStagingLakehouse, "Lakehouse")}/Files/{WarehouseStagingPath}".TrimEnd('/');

    /// <summary>
    /// OneLake path prefix of one Delta table in the Lakehouse's managed <c>Tables/</c> area, e.g.
    /// <c>Sales.Lakehouse/Tables/Patient</c> — or <c>Sales.Lakehouse/Tables/gold/Patient</c> when
    /// <see cref="LakehouseSchema"/> is set for a schema-enabled Lakehouse.
    ///
    /// <para>Note this is <c>Tables/</c>, the one prefix <see cref="NormalizeBasePath"/> refuses for
    /// <see cref="FabricLandingMode.OneLakeFiles"/>. The refusal is not about the path being dangerous — it is
    /// that bare files there never become a table. This mode writes the <c>_delta_log</c> that does, so it is the
    /// one surface allowed to address <c>Tables/</c>, and it ignores <see cref="BasePath"/> entirely: a table's
    /// location is decided by its name and schema, not by a file path the user chose.</para>
    /// </summary>
    public string LakehouseTableRootPath(string tableName)
        => string.IsNullOrWhiteSpace(LakehouseSchema)
            ? $"{ItemPathSegment}/Tables/{tableName}"
            : $"{ItemPathSegment}/Tables/{LakehouseSchema.Trim()}/{tableName}";

    /// <summary>
    /// True when the configured name is actually a GUID. Some tenants disable OneLake's friendly-name support, so
    /// a workspace or item must be addressed by id; the blob endpoint is more forgiving than the DFS one, which is
    /// why a file drop can succeed by name while a Warehouse COPY INTO reading the same path fails.
    /// </summary>
    internal static bool IsItemId(string? value) => Guid.TryParse(value?.Trim(), out _);

    /// <summary>A GUID addresses the item directly; a friendly name needs its <c>.{ItemType}</c> suffix.</summary>
    internal static string FormatItemSegment(string? nameOrId, string itemType)
    {
        var value = (nameOrId ?? string.Empty).Trim();
        return IsItemId(value) ? value : $"{value}.{itemType}";
    }

    /// <summary>
    /// The fully-qualified target table for a Warehouse load.
    ///
    /// <para><paramref name="fallbackSchemaName"/> is the schema the MAPPING named (a profile's
    /// DestinationObject is normally "dbo.Patient" — see WarehouseTableLandingStrategy.FallbackTableName,
    /// which splits it). It wins over <see cref="WarehouseSchema"/> because it is the more specific
    /// statement of intent: the destination-level schema is a default for names that carry none. Null when
    /// the mapping named a bare table, in which case the destination's own schema applies as before.</para>
    ///
    /// <para>An explicitly configured <see cref="WarehouseTable"/> still overrides the mapping's table name
    /// entirely, and is paired with <see cref="WarehouseSchema"/> — a destination-level override is a
    /// destination-level statement, so it does not inherit the mapping's schema.</para>
    /// </summary>
    public string QualifiedWarehouseTable(string fallbackTableName, string? fallbackSchemaName = null)
        => string.IsNullOrWhiteSpace(WarehouseTable)
            ? $"[{(string.IsNullOrWhiteSpace(fallbackSchemaName) ? WarehouseSchema : fallbackSchemaName)}].[{fallbackTableName}]"
            : $"[{WarehouseSchema}].[{WarehouseTable}]";

    /// <summary>
    /// The Warehouse TDS connection string, built from whatever the user supplied.
    ///
    /// <para>Fabric's own UI shows a bare SERVER NAME ("xxx.datawarehouse.fabric.microsoft.com"), so pasting
    /// exactly what Fabric displays is the natural thing to do — and handing that to SqlConnection fails with
    /// "Format of the initialization string does not conform to specification starting at index 0", which says
    /// nothing about what to fix. A value with no '=' in it is therefore treated as a host and wrapped into a
    /// real connection string here, rather than rejected. A full connection string is passed through untouched,
    /// with the database defaulted to the Warehouse item name when it omits one.</para>
    /// </summary>
    public string WarehouseConnectionString
    {
        get
        {
            var configured = (WarehouseSqlEndpoint ?? string.Empty).Trim();
            if (configured.Length == 0)
            {
                return configured;
            }

            // No key=value pair anywhere means this is a host, not a connection string.
            if (!configured.Contains('=', StringComparison.Ordinal))
            {
                var host = configured
                    .Replace("tcp:", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/');
                return $"Server={host};Database={ItemName};Encrypt=True;TrustServerCertificate=False";
            }

            return configured.Contains("Database=", StringComparison.OrdinalIgnoreCase)
                || configured.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)
                    ? configured
                    : $"{configured.TrimEnd(';')};Database={ItemName}";
        }
    }

    /// <summary>Only service-principal mode has credential material in Key Vault to resolve.</summary>
    public bool RequiresSecret => AuthMode == FabricAuthMode.ServicePrincipal;

    /// <summary>
    /// The OneLake blob-protocol account URL. The blob endpoint (not <c>dfs</c>) is deliberate: it is what the
    /// <c>Azure.Storage.Blobs</c> client speaks, and OneLake serves both.
    /// </summary>
    /// <remarks>
    /// Blank-checked, not just null-checked: the wizard posts every optional field it renders, so an untouched
    /// override arrives as "" rather than being absent. A plain <c>??</c> therefore accepted the empty string and
    /// produced an empty account URL, which failed only at write time (UriFormatException, "The URI is empty")
    /// while Test Connection — which builds its own URL — still reported Connected. Same reasoning applies to
    /// every other optional string parsed from the metadata bag.
    /// </remarks>
    public string AccountUrl => string.IsNullOrWhiteSpace(AccountUrlOverride)
        ? $"https://onelake.blob.{EndpointSuffix}"
        : AccountUrlOverride;

    /// <summary>The item's own path prefix inside the workspace container, e.g. <c>Sales.Lakehouse</c>.</summary>
    /// <remarks>
    /// A GUID item name is emitted bare — see <see cref="FormatItemSegment"/> for why a tenant with OneLake
    /// friendly names disabled needs that.
    /// </remarks>
    public string ItemPathSegment => FormatItemSegment(ItemName, ItemType);

    /// <summary>Full prefix every written file sits under, e.g. <c>Sales.Lakehouse/Files/fhirbridge</c>.</summary>
    public string RootPath => string.IsNullOrWhiteSpace(BasePath)
        ? $"{ItemPathSegment}/Files"
        : $"{ItemPathSegment}/Files/{BasePath}";

    public string FileExtension => FileFormat switch
    {
        FabricFileFormat.Parquet => "parquet",
        FabricFileFormat.Csv => "csv",
        _ => "ndjson",
    };

    public string ContentType => FileFormat switch
    {
        FabricFileFormat.Parquet => "application/vnd.apache.parquet",
        FabricFileFormat.Csv => "text/csv",
        _ => "application/x-ndjson",
    };

    // A Fabric workspace/item name is far more permissive than an Azure container name (spaces and mixed case are
    // normal), so only genuinely unusable characters are rejected: path separators, which would silently retarget
    // the write at a different item, and control characters.
    private static readonly Regex UnsafeNameCharacters = new(@"[/\\\x00-\x1F\x7F]", RegexOptions.Compiled);

    /// <summary>
    /// Item types this destination can actually write to. Deliberately NOT the full set of Fabric item types:
    /// a <c>KQLDatabase</c> takes Kusto ingest calls and a <c>MirroredDatabase</c> is read-only (it is a managed
    /// replica of an external source), so neither has a <c>Files/</c> area to land a file in. Both used to be
    /// accepted here and then failed at run time, mid-pipeline; they are refused at configuration-parse time
    /// instead. <c>Warehouse</c> stays listed because it is addressable for staging even though
    /// <see cref="FabricLandingMode.WarehouseTable"/> itself is not implemented yet — the mode check above is
    /// what refuses that combination, with a message about the mode rather than the item type.
    /// </summary>
    private static readonly string[] SupportedItemTypes = ["Lakehouse", "Warehouse"];

    public static FabricDestinationSettings Parse(DestinationConfiguration destination)
    {
        var json = destination.ConnectionMetadataJson;

        // DataFabricWarehouse IS the Warehouse surface — the type carries that fact, so the mode is derived from it
        // rather than read back out of connection metadata. This is the whole point of splitting the type (see
        // DestinationType.DataFabricWarehouse): every caller that needs to know "is this a Warehouse?" asks the
        // type, and a stale or absent dest_fabricMode can no longer contradict it. DataFabricAzure keeps reading
        // the metadata exactly as before, defaulting to OneLakeFiles — so nothing about a Files destination moves.
        var mode = destination.DestinationType == DestinationType.DataFabricWarehouse
            ? FabricLandingMode.WarehouseTable
            : ParseEnum(ConnectionMetadataReader.GetString(json, "dest_fabricMode"), FabricLandingMode.OneLakeFiles);
        if (mode == FabricLandingMode.Eventstream)
        {
            // The one mode that is refused here rather than by a missing strategy registration, because it is not
            // "unbuilt" — it is deliberately served elsewhere, and the user needs pointing there. An Eventstream
            // custom endpoint is plain authenticated HTTP, which the Data Lake Webhook destination already speaks;
            // a second, thinner implementation of the same wire protocol would be worse than the redirect.
            throw new NotSupportedException(
                $"Destination '{destination.Name}': Fabric Eventstream is not implemented as a Fabric landing "
                    + "mode. An Eventstream custom endpoint is authenticated HTTP — use the Data Lake Webhook "
                    + "destination with that endpoint URL instead.");
        }

        // Workspace comes from metadata, or from Target for a row created through the "existing connection" path —
        // the same dual read BlobDestinationSettings does for its container name.
        var workspace = FirstNonBlank(
            ConnectionMetadataReader.GetString(json, "dest_fabricWorkspace"),
            destination.Target);
        RequireName(workspace, "workspace (Target or dest_fabricWorkspace)", destination.Name);

        var itemName = ConnectionMetadataReader.GetString(json, "dest_fabricItemName");
        RequireName(itemName, "lakehouse/item name (dest_fabricItemName)", destination.Name);

        var itemType = ConnectionMetadataReader.GetString(json, "dest_fabricItemType") ?? "Lakehouse";
        var canonicalItemType = SupportedItemTypes.FirstOrDefault(
            supported => string.Equals(supported, itemType, StringComparison.OrdinalIgnoreCase));
        if (canonicalItemType is null)
        {
            var reason = string.Equals(itemType, "KQLDatabase", StringComparison.OrdinalIgnoreCase)
                ? " A KQL Database is written through Kusto ingestion, not file drops, so it has no Files area."
                : string.Equals(itemType, "MirroredDatabase", StringComparison.OrdinalIgnoreCase)
                    ? " A Mirrored Database is a read-only replica — write to the source database it mirrors instead."
                    : string.Empty;
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' has unsupported Fabric item type '{itemType}'. Supported: "
                    + string.Join(", ", SupportedItemTypes) + "." + reason);
        }

        var authMode = ParseEnum(
            ConnectionMetadataReader.GetString(json, "dest_fabricAuthMode"), FabricAuthMode.ManagedIdentity);

        var tenantId = ConnectionMetadataReader.GetString(json, "dest_fabricTenantId");
        var clientId = ConnectionMetadataReader.GetString(json, "dest_fabricClientId");
        if (authMode == FabricAuthMode.ServicePrincipal)
        {
            RequireName(tenantId, "tenant id (dest_fabricTenantId)", destination.Name);
            RequireName(clientId, "client id (dest_fabricClientId)", destination.Name);
        }

        // Warehouse mode needs two things OneLake mode does not, and neither can be defaulted: the TDS endpoint
        // (a different service from OneLake, so not derivable from the workspace) and the Lakehouse the COPY INTO
        // stages through (a Warehouse has no Files area of its own). Both are required here so a missing one is a
        // configuration error rather than a failure partway through a load.
        var warehouseSqlEndpoint = ConnectionMetadataReader.GetString(json, "dest_fabricWarehouseSqlEndpoint")?.Trim();
        var stagingLakehouse = ConnectionMetadataReader.GetString(json, "dest_fabricWarehouseStagingLakehouse");
        if (mode == FabricLandingMode.WarehouseTable)
        {
            RequireName(
                warehouseSqlEndpoint,
                "Warehouse SQL endpoint (dest_fabricWarehouseSqlEndpoint)",
                destination.Name);
            RequireName(
                stagingLakehouse,
                "staging lakehouse (dest_fabricWarehouseStagingLakehouse)",
                destination.Name);
        }

        return new FabricDestinationSettings(
            Mode: mode,
            AuthMode: authMode,
            Workspace: workspace!.Trim(),
            ItemName: itemName!.Trim(),
            ItemType: canonicalItemType,
            BasePath: NormalizeBasePath(
                ConnectionMetadataReader.GetString(json, "dest_fabricPath"), destination.Name),
            FileFormat: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_fabricFileFormat"), FabricFileFormat.Ndjson),
            Partitioning: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_fabricPartitionBy"), FabricPartitionScheme.ResourceType),
            TenantId: tenantId,
            ClientId: clientId,
            // NullIfBlank on every optional string: the wizard posts each field it renders, so an untouched
            // one arrives as "" rather than absent. Normalising here means no downstream consumer has to
            // remember the difference — the bug this fixes was an empty override producing an empty account URL.
            ManagedIdentityClientId: NullIfBlank(
                ConnectionMetadataReader.GetString(json, "dest_fabricManagedIdentityClientId")),
            AuthorityHost: NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_fabricAuthorityHost")),
            EndpointSuffix: FirstNonBlank(
                ConnectionMetadataReader.GetString(json, "dest_fabricEndpointSuffix"), "fabric.microsoft.com")!,
            AccountUrlOverride: NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_fabricAccountUrl")),
            WarehouseSqlEndpoint: warehouseSqlEndpoint,
            WarehouseSchema: FirstNonBlank(
                ConnectionMetadataReader.GetString(json, "dest_fabricWarehouseSchema"), "dbo")!.Trim(),
            WarehouseTable: NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_fabricWarehouseTable")),
            WarehouseWriteMode: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_fabricWarehouseWriteMode"),
                FabricTableWriteMode.Append),
            WarehouseStagingLakehouse: stagingLakehouse?.Trim(),
            WarehouseUseWorkspaceIdentity: string.Equals(
                ConnectionMetadataReader.GetString(json, "dest_fabricWarehouseUseWorkspaceIdentity"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            WarehouseStagingPath: NormalizeStagingPath(
                ConnectionMetadataReader.GetString(json, "dest_fabricWarehouseStagingPath")),
            LakehouseSchema: NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_fabricLakehouseSchema")));
    }

    /// <summary>
    /// Normalizes the configured path to a clean relative path under the item's <c>Files/</c> area, and refuses the
    /// two shapes that would produce a broken result:
    /// <list type="bullet">
    /// <item><description><c>Tables/...</c> — a Fabric Lakehouse table is a Delta table, defined by its
    /// <c>_delta_log</c> transaction log. Dropping bare Parquet or NDJSON into <c>Tables/</c> does not register a
    /// table: it produces an unmanaged folder Fabric reports as an unidentified area, and the customer sees a table
    /// that never appears. This writer does not produce a Delta log, so the path is rejected with that explanation
    /// instead of appearing to succeed.</description></item>
    /// <item><description>An absolute URL or a path re-stating <c>{item}.Lakehouse</c> — the caller is trying to
    /// address the item twice, which would nest a second item folder inside the first.</description></item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Staging folder under the staging Lakehouse's Files area. Looser than <see cref="NormalizeBasePath"/> —
    /// nothing user-facing reads these files, they are deleted after each load — but still relative-only, so a
    /// pasted URL cannot retarget the staging write.
    /// </summary>
    internal static string NormalizeStagingPath(string? configuredPath)
    {
        var path = (configuredPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal))
        {
            return "_staging";
        }

        if (path.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["Files/".Length..].Trim('/');
        }

        return path.Length == 0 ? "_staging" : path;
    }

    internal static string NormalizeBasePath(string? configuredPath, string destinationName)
    {
        var path = (configuredPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0)
        {
            return "fhirbridge";
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': dest_fabricPath must be a relative path under the item's Files "
                    + $"area, not a full URL ('{configuredPath}').");
        }

        // Tolerate a path the user pasted from the Fabric UI with a leading "Files/" already on it.
        if (path.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["Files/".Length..].Trim('/');
        }

        if (path.Equals("Tables", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("Tables/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': the Lakehouse 'Tables/' area holds Delta tables, and this "
                    + "destination writes plain files (no Delta transaction log), so files landed there would never "
                    + "register as a table. Write to the Files area instead and surface it as a table with a Fabric "
                    + "shortcut, notebook or pipeline.");
        }

        if (path.Contains(".Lakehouse", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Warehouse", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': dest_fabricPath must not repeat the item name — the item is "
                    + "already addressed by dest_fabricItemName/dest_fabricItemType.");
        }

        if (path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': dest_fabricPath must not contain '.' or '..' segments.");
        }

        return path;
    }

    /// <summary>Treats a blank optional field as absent. See the remarks on <see cref="AccountUrl"/>.</summary>
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RequireName(string? value, string label, string destinationName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Destination '{destinationName}' has no {label} configured.");
        }

        if (UnsafeNameCharacters.IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' has an invalid {label}: '{value}' — it cannot contain a path "
                    + "separator or control characters.");
        }
    }

    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
