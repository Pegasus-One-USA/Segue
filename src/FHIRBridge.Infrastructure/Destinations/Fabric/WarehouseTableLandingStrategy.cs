using System.Globalization;
using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Lands mapped records as ROWS in a Fabric Warehouse table, the surface
/// <see cref="FabricLandingMode.OneLakeFiles"/> deliberately cannot offer: a file drop has nothing to match on, so
/// it can never upsert, and its output is not queryable until someone promotes it to a table by hand.
///
/// <para><b>Why a load has two halves.</b> A Warehouse has no Files area of its own and no row-by-row bulk API, so
/// rows arrive by staging Parquet somewhere both services can address — a Lakehouse in the same workspace — and
/// then issuing <c>COPY INTO</c> over TDS. That is why this strategy needs both an OneLake client and a SQL
/// connection, and why the staging Lakehouse is required configuration rather than a default.</para>
///
/// <para><b>The customer owns the table.</b> This never creates or alters the target
/// (<c>docs/backend/11-destination-schema-ownership-plan.md</c>); a missing table is an error naming the table,
/// not a CREATE. Note the staged Parquet is written all-<c>nvarchar</c>, because the mapping pipeline hands every
/// cell over already stringly-typed (see <c>MappedDestinationSerialization.GetCell</c>) — so the customer's column
/// types must be ones COPY INTO can convert a string into.</para>
///
/// <para><b>Unverified against a live Fabric tenant.</b> The T-SQL below is the documented shape for a Warehouse
/// COPY INTO from OneLake, but it has not been run against a real Warehouse. The three things most likely to need
/// adjusting on first contact: the COPY INTO credential clause (this relies on the connection's own Entra identity
/// rather than naming a credential), the ABFSS staging URL form, and whether the identity needs grants on the
/// staging Lakehouse separately from the Warehouse.</para>
/// </summary>
internal sealed class WarehouseTableLandingStrategy : IFabricLandingStrategy
{
    private readonly IOneLakeClientFactory _clientFactory;
    private readonly IFabricWarehouseConnectionFactory _connectionFactory;
    private readonly ILogger<WarehouseTableLandingStrategy> _logger;

    public WarehouseTableLandingStrategy(
        IOneLakeClientFactory clientFactory,
        IFabricWarehouseConnectionFactory connectionFactory,
        ILogger<WarehouseTableLandingStrategy> logger)
    {
        _clientFactory = clientFactory;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public FabricLandingMode Handles => FabricLandingMode.WarehouseTable;

    /// <summary>
    /// Warehouse only. A Lakehouse's SQL analytics endpoint looks similar and is NOT a substitute: it is read-only,
    /// so a COPY INTO against it fails. Landing rows in a Lakehouse means Delta
    /// (<see cref="FabricLandingMode.LakehouseTable"/>), not this.
    /// </summary>
    public IReadOnlySet<string> SupportedItemTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Warehouse" };

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        FabricDestinationSettings settings,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var columns = MappedDestinationSerialization.GetColumns(records);
        if (columns.Count == 0)
        {
            // No mapped columns means nothing to COPY INTO; creating an empty table would be worse than saying so.
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': mapping profile '{mappingProfile.Name}' produced no columns, so "
                    + "there is nothing to load into the Warehouse.");
        }

        var (fallbackSchema, fallbackTable) = FallbackTableName(mappingProfile);
        var table = settings.QualifiedWarehouseTable(fallbackTable, fallbackSchema);
        var stagingBlobPath = BuildStagingBlobPath(settings, mappingProfile, DateTime.UtcNow);

        var workspace = await _clientFactory.GetWorkspaceAsync(destination, settings, cancellationToken);
        var stagingBlob = workspace.Container.GetBlobClient(stagingBlobPath);

        var payload = await MappedDestinationParquetSerializer.SerializeAsync(records, cancellationToken);
        using (var stream = new MemoryStream(payload))
        {
            await stagingBlob.UploadAsync(stream, overwrite: true, cancellationToken);
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(destination, settings, cancellationToken);

            await EnsureTableExistsAsync(connection, table, cancellationToken);

            var stagingUrl = BuildStagingUrl(settings, stagingBlobPath);
            var loaded = settings.WarehouseWriteMode == FabricTableWriteMode.Upsert
                ? await CopyThenMergeAsync(connection, settings, table, columns, mappingProfile, stagingUrl, cancellationToken)
                : await CopyIntoAsync(
                    connection, table, columns, stagingUrl, settings.WarehouseUseWorkspaceIdentity, cancellationToken);

            _logger.LogInformation(
                "Loaded {RecordCount} {ResourceType} record(s) into Fabric Warehouse {Table} ({WriteMode}) for "
                    + "destination {DestinationId}.",
                loaded,
                mappingProfile.ResourceType,
                table,
                settings.WarehouseWriteMode,
                destination.Id);

            return new DestinationWriteResult(loaded);
        }
        finally
        {
            // Staging files are pure intermediate state and carry PHI, so they are removed whether the load
            // succeeded or threw. A cleanup failure must not mask the original error, hence the swallow-and-log:
            // the leftover is a tidiness problem, the load failure is the one worth surfacing.
            try
            {
                await stagingBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
            catch (Exception cleanupFailure)
            {
                _logger.LogWarning(
                    cleanupFailure,
                    "Could not delete Fabric Warehouse staging file {StagingPath} for destination {DestinationId}; "
                        + "it holds mapped record data and should be removed.",
                    stagingBlobPath,
                    destination.Id);
            }
        }
    }

    /// <summary>
    /// Verifies the target table exists, and fails with an actionable message when it does not.
    ///
    /// <para>FHIRBridge never owns a customer's destination schema
    /// (<c>docs/backend/11-destination-schema-ownership-plan.md</c>): writers only write into columns the customer
    /// explicitly mapped, and creating a table here would silently make FHIRBridge the owner of its shape — the
    /// exact behaviour section 3.A.1-2 removed from <c>MappedSqlServerDestinationWriter</c>. So this checks and
    /// throws, matching that writer's wording, rather than issuing DDL.</para>
    /// </summary>
    private static async Task EnsureTableExistsAsync(
        SqlConnection connection,
        string qualifiedTable,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT OBJECT_ID(@table, N'U');", connection);
        command.Parameters.AddWithValue("@table", qualifiedTable);

        var objectId = await command.ExecuteScalarAsync(cancellationToken);
        if (objectId is null || objectId == DBNull.Value)
        {
            throw new InvalidOperationException(
                $"Destination table {qualifiedTable} does not exist in the Fabric Warehouse. Create it in your "
                    + "warehouse before running this pipeline.");
        }
    }

    /// <summary>
    /// Append path: one COPY INTO straight at the target table.
    ///
    /// <para>Rows loaded are reported via <c>@@ROWCOUNT</c> in the same batch. Counting the table instead would
    /// return its total size, not this load's contribution — wrong on any table that already held rows, which a
    /// customer-provisioned table generally does.</para>
    /// </summary>
    private static async Task<int> CopyIntoAsync(
        SqlConnection connection,
        string qualifiedTable,
        IReadOnlyList<string> columns,
        string stagingUrl,
        bool useWorkspaceIdentity,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            BuildCopyInto(qualifiedTable, columns, stagingUrl, useWorkspaceIdentity)
                + Environment.NewLine + "SELECT @@ROWCOUNT;",
            connection);

        var loaded = await command.ExecuteScalarAsync(cancellationToken);
        return loaded is int count ? count : 0;
    }

    /// <summary>
    /// Upsert path: COPY INTO a per-load staging table, MERGE it into the target, drop it. The staging table is
    /// what makes the upsert atomic from a reader's point of view — matched rows update and new rows insert in one
    /// statement, rather than a delete-then-insert window where the row is briefly missing from a live report.
    /// </summary>
    private static async Task<int> CopyThenMergeAsync(
        SqlConnection connection,
        FabricDestinationSettings settings,
        string qualifiedTable,
        IReadOnlyList<string> columns,
        MappingProfile mappingProfile,
        string stagingUrl,
        CancellationToken cancellationToken)
    {
        var keyColumn = ResolveKeyColumn(mappingProfile, columns);
        var stagingTable = $"[{settings.WarehouseSchema}].[__fhirbridge_stage_{Guid.NewGuid():N}]";

        var columnList = string.Join(", ", columns.Select(column => $"[{Escape(column)}]"));
        var updateList = string.Join(
            ", ",
            columns.Where(column => !string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase))
                .Select(column => $"target.[{Escape(column)}] = source.[{Escape(column)}]"));

        // ddl-allowed: this is FHIRBridge's own per-load scratch table (a guid-suffixed __fhirbridge_stage_* name),
        // created and dropped inside this one call — not part of the customer's schema, which
        // docs/backend/11-destination-schema-ownership-plan.md reserves to the customer. A Warehouse has no
        // temp-table equivalent that survives the COPY INTO, so the MERGE source has to be a real table; the
        // alternative (COPY INTO the target then de-duplicate in place) would briefly expose duplicate rows to
        // anyone querying it.
        await using (var create = new SqlCommand(
            $"CREATE TABLE {stagingTable} ("
                + string.Join(", ", columns.Select(column => $"[{Escape(column)}] nvarchar(4000) NULL"))
                + ");",
            connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using (var copy = new SqlCommand(
                BuildCopyInto(stagingTable, columns, stagingUrl, settings.WarehouseUseWorkspaceIdentity),
                connection))
            {
                await copy.ExecuteNonQueryAsync(cancellationToken);
            }

            // No UPDATE clause when the key is the only mapped column — MERGE rejects an empty SET list, and
            // "update the key to itself" is a no-op worth skipping rather than generating.
            var updateClause = string.IsNullOrEmpty(updateList)
                ? string.Empty
                : $"WHEN MATCHED THEN UPDATE SET {updateList}{Environment.NewLine}";

            var merge = $"""
                MERGE {qualifiedTable} AS target
                USING {stagingTable} AS source
                ON target.[{Escape(keyColumn)}] = source.[{Escape(keyColumn)}]
                {updateClause}WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({string.Join(", ", columns.Select(column => $"source.[{Escape(column)}]"))});
                """;

            await using var mergeCommand = new SqlCommand(merge, connection);
            return await mergeCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            // ddl-allowed: drops only the scratch table created above, never a customer object.
            await using var drop = new SqlCommand($"DROP TABLE IF EXISTS {stagingTable};", connection);
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// COPY INTO with no CREDENTIAL clause, which means "use the identity of the current connection". That is
    /// deliberate: the destination already carries one Entra identity, and naming a second credential here would
    /// create a configuration that authenticates to the Warehouse as one principal and reads staging as another.
    /// </summary>
    /// <summary>
    /// The COPY INTO for one staged Parquet file.
    ///
    /// <para>With <paramref name="useWorkspaceIdentity"/> false (the default) no CREDENTIAL clause is emitted,
    /// which is the documented default: the executing identity's own Entra token authorizes the source read. It
    /// therefore needs Contributor on the workspace holding the staging Lakehouse. With it true, COPY INTO
    /// impersonates the workspace identity for the source read only — the statement still runs in the caller's
    /// SQL security context — so the caller needs no direct permission on the staged file.</para>
    ///
    /// <para>No SECRET is ever emitted: the two credential forms that take one (SAS, Storage Account Key) do not
    /// apply to a OneLake source, which supports only Microsoft Entra ID and Workspace Identity.</para>
    /// </summary>
    internal static string BuildCopyInto(
        string qualifiedTable,
        IReadOnlyList<string> columns,
        string stagingUrl,
        bool useWorkspaceIdentity)
    {
        var credential = useWorkspaceIdentity
            ? ", CREDENTIAL = (IDENTITY = 'Workspace Identity')"
            : string.Empty;

        return $"""
            COPY INTO {qualifiedTable} ({string.Join(", ", columns.Select(column => $"[{Escape(column)}]"))})
            FROM '{stagingUrl.Replace("'", "''")}'
            WITH (FILE_TYPE = 'PARQUET'{credential});
            """;
    }

    /// <summary>
    /// The staged file's location in the form COPY INTO accepts for a OneLake source:
    /// <c>https://onelake.dfs.&lt;suffix&gt;/&lt;workspace&gt;/&lt;item&gt;.Lakehouse/Files/...</c>.
    ///
    /// <para>Deliberately NOT the <c>abfss://</c> form. The docs give the OneLake external location as an
    /// https URL (see "Use COPY INTO with OneLake" in the COPY INTO T-SQL reference), and the abfss form this
    /// used to emit failed against a live tenant with "Access token couldn't be fetched for storage path ... as
    /// it's an unsupported URL or cause of a transient error" — the engine reporting the scheme back as https
    /// while refusing it. The upload still uses the blob endpoint (that is the Azure.Storage.Blobs client's own
    /// protocol); only what COPY INTO is handed changes here.</para>
    /// </summary>
    internal static string BuildStagingUrl(FabricDestinationSettings settings, string stagingBlobPath)
        => $"https://onelake.dfs.{settings.EndpointSuffix}/{settings.Workspace}/{stagingBlobPath}";

    /// <summary>
    /// Staging path is per-load unique (guid), so two concurrent runs of the same route cannot read each other's
    /// file — the failure that would otherwise show up as a duplicated or truncated load under parallelism.
    /// </summary>
    internal static string BuildStagingBlobPath(
        FabricDestinationSettings settings, MappingProfile mappingProfile, DateTime timestampUtc)
        => string.Join(
            '/',
            settings.WarehouseStagingRootPath,
            $"{Sanitize(mappingProfile.ResourceType)}_{timestampUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}.parquet");

    /// <summary>
    /// Same key convention the SQL Server writer uses — the mapping field flagged as the upsert key, else
    /// <c>SourceResourceId</c> — so a user who has configured an upsert key for a SQL destination does not have to
    /// learn a second rule for Fabric.
    /// </summary>
    private static string ResolveKeyColumn(MappingProfile mappingProfile, IReadOnlyList<string> columns)
    {
        // Scoped exactly as MappedSqlServerDestinationWriter.ResolveUpsertKeyColumn scopes it: enabled fields only,
        // and only those whose resource type / destination object match this profile (blank meaning "any"). A
        // profile can carry fields for several scopes, so an unscoped FirstOrDefault would pick up another table's
        // key and merge on the wrong column.
        var configured = mappingProfile.Fields.FirstOrDefault(field =>
            field.IsUpsertKey
            && field.IsEnabled
            && (string.IsNullOrWhiteSpace(field.ResourceType)
                || string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(field.DestinationObject)
                || string.Equals(
                    field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase)))
            ?.TargetField;

        if (!string.IsNullOrWhiteSpace(configured)
            && columns.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            return configured;
        }

        var fallback = columns.FirstOrDefault(
            column => string.Equals(column, "SourceResourceId", StringComparison.OrdinalIgnoreCase));

        return fallback ?? throw new InvalidOperationException(
            "Upsert needs a key column: flag a mapping field as the upsert key, or map SourceResourceId. Mapped "
                + $"columns were: {string.Join(", ", columns)}.");
    }

    /// <summary>
    /// The target table a mapping profile names, split into schema and table.
    ///
    /// <para>The profile's DestinationObject is normally SCHEMA-QUALIFIED ("dbo.Patient"), exactly as the
    /// mapping canvas writes it and as MappedSqlServerDestinationWriter.ParseDestinationObject reads it. This
    /// used to run the whole string through <see cref="Sanitize"/>, which turns every non-alphanumeric
    /// character — the dot included — into an underscore, producing "dbo_Patient" and then, once
    /// QualifiedWarehouseTable applied the schema again, the table "[dbo].[dbo_Patient]" that no warehouse
    /// has. Splitting first (same rule the SQL Server writer uses) is what makes the name the writer looks
    /// for the same name the canvas created.</para>
    ///
    /// <para>Sanitizing still happens, but PER PART, so the injection boundary is unchanged.</para>
    /// </summary>
    private static (string? SchemaName, string TableName) FallbackTableName(MappingProfile mappingProfile)
    {
        var stem = mappingProfile.DestinationObject;

        // "dbo.Patient;mode=upsert" — the write-mode suffix is not part of the name.
        var suffixIndex = stem.IndexOf(';', StringComparison.Ordinal);
        if (suffixIndex >= 0)
        {
            stem = stem[..suffixIndex];
        }

        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (schemaPart, tablePart) = parts.Length >= 2
            ? (Sanitize(parts[^2]), Sanitize(parts[^1]))
            : (null, Sanitize(parts.Length == 1 ? parts[0] : string.Empty));

        return tablePart.Length == 0
            ? (schemaPart, Sanitize(mappingProfile.ResourceType))
            : (schemaPart, tablePart);
    }

    /// <summary>
    /// Identifier hardening. Every identifier here is bracket-quoted, so doubling a closing bracket is what makes
    /// that quoting hold; the rest is stripped rather than escaped because a mapped column name containing a
    /// newline or a bracket is a mapping mistake, not something to faithfully reproduce in DDL.
    /// </summary>
    private static string Escape(string identifier)
        => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        return builder.ToString().Trim('_');
    }
}
