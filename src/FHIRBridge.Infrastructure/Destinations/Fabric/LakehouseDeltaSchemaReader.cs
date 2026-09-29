using System.Text.Json;
using Azure.Storage.Blobs;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Reads the tables (and their columns) that already exist in a Lakehouse's <c>Tables/</c> area, so the mapping
/// canvas can offer the same "pick an existing table" list it offers for SQL Server, Mongo and Fabric Warehouse.
///
/// <para><b>Why this is not the SQL probe.</b> A Warehouse has a TDS endpoint whose catalog can be queried. A
/// Lakehouse's <c>Tables/</c> area is blob storage — there is nothing to query, so a table is discovered by
/// LISTING: a folder directly under <c>Tables/</c> that contains a <c>_delta_log</c> is a Delta table, and a
/// folder without one is not (Fabric shows those as an "unidentified area"). That is the whole discovery rule.</para>
///
/// <para><b>Columns come from the log, not from the data.</b> The current schema is the <c>schemaString</c> of the
/// most recent <c>metaData</c> action, which is authoritative in a way the Parquet files are not: a table whose
/// schema changed has old files with the old columns, so reading a data file would report a schema that may no
/// longer be the table's. Only commits are read, never the Parquet.</para>
///
/// <para><b>Unverified against a live Fabric tenant</b>, like the writer it complements.</para>
/// </summary>
internal static class LakehouseDeltaSchemaReader
{
    /// <summary>
    /// How many commit files are read backwards looking for the newest <c>metaData</c>. A schema change writes a
    /// new metaData action, so the newest one is usually at or near the latest version — but an append-only
    /// table has exactly one, at version 0, and reading every commit of a long-lived table just to find it would
    /// make listing the picker slow. Past this, the table is listed with no columns rather than not at all:
    /// naming a table you cannot expand is far more useful than omitting it.
    /// </summary>
    private const int MaxCommitsScannedForSchema = 25;

    /// <summary>
    /// Every Delta table directly under the item's <c>Tables/</c> area, or under <c>Tables/{schema}/</c> for a
    /// schema-enabled Lakehouse.
    /// </summary>
    internal static async Task<IReadOnlyList<DestinationTableSchemaDto>> ListTablesAsync(
        BlobContainerClient workspace,
        FabricDestinationSettings settings,
        CancellationToken cancellationToken)
    {
        var tablesRoot = string.IsNullOrWhiteSpace(settings.LakehouseSchema)
            ? $"{settings.ItemPathSegment}/Tables/"
            : $"{settings.ItemPathSegment}/Tables/{settings.LakehouseSchema.Trim()}/";

        // One flat listing rather than a walk: OneLake has no real directories, so asking for every blob whose
        // name starts with a "<table>/_delta_log/" segment finds all of them in a single pass. A delimited
        // listing would need one round trip per candidate folder just to discover whether it has a log.
        var logPrefixes = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var blob in workspace.GetBlobsAsync(
            prefix: tablesRoot, cancellationToken: cancellationToken))
        {
            var relative = blob.Name[tablesRoot.Length..];
            var logIndex = relative.IndexOf("/_delta_log/", StringComparison.Ordinal);
            if (logIndex <= 0)
            {
                continue;
            }

            var tableName = relative[..logIndex];

            // A nested path ("gold/Patient") means a schema level this destination is not configured for, so it
            // is not this destination's table — including it would offer a target the writer cannot address.
            if (!tableName.Contains('/', StringComparison.Ordinal))
            {
                logPrefixes.Add(tableName);
            }
        }

        var tables = new List<DestinationTableSchemaDto>();
        foreach (var tableName in logPrefixes.Order(StringComparer.OrdinalIgnoreCase))
        {
            var columns = await ReadColumnsAsync(
                workspace, $"{tablesRoot}{tableName}/_delta_log/", cancellationToken);

            tables.Add(new DestinationTableSchemaDto(
                SchemaName: settings.LakehouseSchema ?? string.Empty,
                TableName: tableName,
                FullName: tableName,
                Columns: columns));
        }

        return tables;
    }

    /// <summary>
    /// The table's current columns, from the newest <c>metaData</c> action in its log.
    ///
    /// <para>Commits are read newest-first and the search stops at the first metaData found, because a later
    /// metaData completely replaces an earlier one — reading oldest-first would report the schema the table was
    /// created with rather than the one it has.</para>
    /// </summary>
    private static async Task<IReadOnlyList<DestinationColumnSchemaDto>> ReadColumnsAsync(
        BlobContainerClient workspace,
        string logPrefix,
        CancellationToken cancellationToken)
    {
        var commitNames = new List<string>();
        await foreach (var blob in workspace.GetBlobsAsync(
            prefix: logPrefix, cancellationToken: cancellationToken))
        {
            var name = blob.Name[logPrefix.Length..];

            // Commit files only — a checkpoint (.checkpoint.parquet) and _last_checkpoint share this folder and
            // carry no metaData action in a form this reads.
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !name.Contains('/', StringComparison.Ordinal))
            {
                commitNames.Add(name);
            }
        }

        // Zero-padded to 20 digits, so an ordinal sort IS version order — which is the reason the protocol pads
        // them in the first place.
        commitNames.Sort(StringComparer.Ordinal);

        foreach (var commitName in commitNames.AsEnumerable().Reverse().Take(MaxCommitsScannedForSchema))
        {
            var columns = await TryReadSchemaFromCommitAsync(
                workspace, logPrefix + commitName, cancellationToken);

            if (columns is not null)
            {
                return columns;
            }
        }

        // A table whose schema could not be read is still a real table worth offering; the caller shows it with
        // no columns rather than dropping it from the list.
        return [];
    }

    private static async Task<IReadOnlyList<DestinationColumnSchemaDto>?> TryReadSchemaFromCommitAsync(
        BlobContainerClient workspace,
        string commitPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var download = await workspace.GetBlobClient(commitPath).DownloadContentAsync(cancellationToken);
            var commit = download.Value.Content.ToString();

            // Newline-delimited JSON: one action per line, and at most one metaData per commit.
            foreach (var line in commit.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("metaData", out var metaData)
                    || !metaData.TryGetProperty("schemaString", out var schemaString)
                    || schemaString.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                return ParseSchemaString(schemaString.GetString());
            }

            return null;
        }
        catch (Exception)
        {
            // A commit that cannot be read or parsed must not fail the whole listing: the picker's job is to
            // name the tables that exist, and one unreadable log costs that table its columns, not its row.
            return null;
        }
    }

    /// <summary>
    /// Delta's schemaString is a JSON <i>string</i> holding a Spark struct — see
    /// <see cref="DeltaTransactionLog.BuildSchemaString"/>, which writes the same shape.
    /// </summary>
    internal static IReadOnlyList<DestinationColumnSchemaDto>? ParseSchemaString(string? schemaString)
    {
        if (string.IsNullOrWhiteSpace(schemaString))
        {
            return null;
        }

        using var schema = JsonDocument.Parse(schemaString);
        if (!schema.RootElement.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var columns = new List<DestinationColumnSchemaDto>();
        foreach (var field in fields.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // A nested type (struct/array/map) is an object here rather than a type name. Reported by its shape
            // rather than skipped: the column genuinely exists, and hiding it would make the picker disagree
            // with the table.
            var deltaType = field.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString() ?? "string"
                : "struct";

            var nullable = !field.TryGetProperty("nullable", out var isNullable)
                || isNullable.ValueKind != JsonValueKind.False;

            columns.Add(new DestinationColumnSchemaDto(
                Name: name.GetString()!,
                DataType: deltaType,
                MappingValueType: MapValueType(deltaType),
                IsNullable: nullable,
                MaxLength: null,
                // A Delta table has no primary key, no uniqueness constraint and no generated columns of the
                // kind this DTO means, so these stay false rather than being guessed from column names.
                IsPrimaryKey: false,
                IsUnique: false,
                IsAutoGenerated: false,
                IsForeignKey: false,
                References: null));
        }

        return columns;
    }

    /// <summary>
    /// Delta primitive type to the mapping canvas's own value types. Anything unrecognized (including every
    /// nested type) maps to String, which is what the writer emits for it anyway.
    /// </summary>
    internal static string MapValueType(string deltaType) => deltaType switch
    {
        "boolean" => "Boolean",
        "byte" or "short" or "integer" or "long" => "Integer",
        "float" or "double" => "Decimal",
        "date" => "Date",
        "timestamp" or "timestamp_ntz" => "DateTime",
        _ when deltaType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) => "Decimal",
        _ => "String",
    };
}
