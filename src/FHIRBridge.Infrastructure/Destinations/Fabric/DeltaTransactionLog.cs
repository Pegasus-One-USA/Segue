using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Builds the <c>_delta_log</c> entries that turn a folder of Parquet files into a Delta table — the one thing
/// separating <see cref="FabricLandingMode.LakehouseTable"/> from <see cref="FabricLandingMode.OneLakeFiles"/>.
/// A Fabric Lakehouse registers a folder under <c>Tables/</c> as a table only when this log is present; without it
/// the folder shows up as an "unidentified area" and the table the customer expected never appears.
///
/// <para><b>Written by hand rather than through a Delta library.</b> The .NET Delta bindings are native-interop
/// wrappers around the Rust kernel, which adds a per-platform native asset to a deployment that is currently pure
/// managed code, and their licence position needed review before adoption. Against that, what an append-only
/// writer actually needs from the protocol is small and completely specified: a protocol action, a metaData
/// action, and one add action per data file, as newline-delimited JSON in a zero-padded commit file. That is the
/// subset implemented here, following the Delta Transaction Log Protocol
/// (https://github.com/delta-io/delta/blob/master/PROTOCOL.md).</para>
///
/// <para><b>Append-only on purpose.</b> This emits <c>add</c> actions and never <c>remove</c>, so it cannot
/// express an update or a delete. Upsert into a Delta table means rewriting the data files that contain matched
/// rows and committing add+remove together, which in turn needs read-side Parquet and conflict resolution against
/// concurrent writers — a materially larger piece of work than appending, and deliberately not attempted here.
/// A customer who needs upsert semantics today should use <see cref="FabricLandingMode.WarehouseTable"/>, whose
/// MERGE runs inside the Warehouse engine.</para>
///
/// <para><b>Concurrency.</b> Delta's commit protocol relies on the destination refusing to overwrite an existing
/// commit file, so two writers racing for the same version produce one winner and one retryable failure. OneLake
/// supports that through a conditional (If-None-Match) blob write; see
/// <c>LakehouseTableLandingStrategy</c>, which performs the commit and owns the retry.</para>
///
/// <para><b>Unverified against a live Fabric tenant.</b> The shapes below follow the protocol specification, but
/// no table written by this code has yet been opened in Fabric. See the class remarks on
/// <c>LakehouseTableLandingStrategy</c> for what to check first if a table does not register.</para>
/// </summary>
internal static class DeltaTransactionLog
{
    /// <summary>
    /// Reader 1 / writer 2 — the plain, featureless Delta table. Deliberately the lowest pair that supports what
    /// is written here: every higher version exists to gate a feature (column mapping, deletion vectors, row
    /// tracking) this writer does not emit, and claiming one would only narrow the set of engines that can read
    /// the result.
    /// </summary>
    private const int MinReaderVersion = 1;
    private const int MinWriterVersion = 2;

    /// <summary>Commit files are named by version, zero-padded to 20 digits (protocol §Delta Log Entries).</summary>
    internal static string CommitFileName(long version)
        => version.ToString("D20", CultureInfo.InvariantCulture) + ".json";

    /// <summary>
    /// The first commit of a table: protocol, metaData, then the add actions for the files just written.
    ///
    /// <para>The protocol requires the first version to carry a metaData action, so table creation is not a
    /// separate step — writing version 0 IS creating the table. That is why this writer can land rows in a
    /// Lakehouse that has no such table yet, where the Warehouse strategy must refuse a missing table: there, the
    /// schema is a customer-owned object in a database FHIRBridge does not own, and creating it would silently
    /// make FHIRBridge the owner of its shape (docs/backend/11-destination-schema-ownership-plan.md). Here the
    /// table IS the folder this destination is configured to write, and its schema is exactly the mapping — there
    /// is no pre-existing customer object to take ownership of.</para>
    /// </summary>
    internal static string BuildInitialCommit(
        Guid tableId,
        string? tableName,
        IReadOnlyList<string> columns,
        IReadOnlyList<DeltaAddedFile> addedFiles,
        DateTimeOffset timestamp)
    {
        var builder = new StringBuilder();

        AppendAction(builder, "protocol", new JsonObject
        {
            ["minReaderVersion"] = MinReaderVersion,
            ["minWriterVersion"] = MinWriterVersion,
        });

        var metaData = new JsonObject
        {
            ["id"] = tableId.ToString(),
            ["format"] = new JsonObject
            {
                ["provider"] = "parquet",
                ["options"] = new JsonObject(),
            },
            // schemaString is a STRING holding serialized JSON, not a nested object. Emitting the object inline
            // produces a log every Delta reader rejects, and it is the easiest field in the protocol to get wrong.
            ["schemaString"] = BuildSchemaString(columns),
            ["partitionColumns"] = new JsonArray(),
            ["configuration"] = new JsonObject(),
            ["createdTime"] = timestamp.ToUnixTimeMilliseconds(),
        };

        if (!string.IsNullOrWhiteSpace(tableName))
        {
            metaData["name"] = tableName;
        }

        AppendAction(builder, "metaData", metaData);
        AppendAddActions(builder, addedFiles, timestamp);

        return builder.ToString();
    }

    /// <summary>
    /// A subsequent commit: add actions only. No metaData action, because the table's schema is unchanged — and
    /// re-stating it would overwrite the current metadata wholesale rather than merge, so an omission here is
    /// safer than a repetition.
    /// </summary>
    internal static string BuildAppendCommit(
        IReadOnlyList<DeltaAddedFile> addedFiles, DateTimeOffset timestamp)
    {
        var builder = new StringBuilder();
        AppendAddActions(builder, addedFiles, timestamp);

        return builder.ToString();
    }

    private static void AppendAddActions(
        StringBuilder builder, IReadOnlyList<DeltaAddedFile> addedFiles, DateTimeOffset timestamp)
    {
        foreach (var file in addedFiles)
        {
            AppendAction(builder, "add", new JsonObject
            {
                // Relative to the table root, so the table survives being moved or mounted at another path.
                ["path"] = file.RelativePath,
                // Required even with no partitioning, in which case it is an empty map rather than absent.
                ["partitionValues"] = new JsonObject(),
                ["size"] = file.SizeBytes,
                ["modificationTime"] = timestamp.ToUnixTimeMilliseconds(),
                // True: this commit genuinely adds rows. False would tell a streaming reader the commit only
                // rearranges existing data, and it would skip these files.
                ["dataChange"] = true,
                // stats is optional. Min/max per column would let a reader skip files, but every column here is a
                // nullable string (see MappedDestinationParquetSerializer), so string min/max would rarely prune
                // anything while making every commit depend on a second pass over the data. numRecords alone is
                // cheap and is what a reader uses to answer COUNT(*) without opening the Parquet.
                ["stats"] = JsonSerializer.Serialize(new JsonObject { ["numRecords"] = file.RecordCount }),
            });
        }
    }

    /// <summary>
    /// The table schema in Delta's subset of Spark's JSON schema format, serialized to a string.
    ///
    /// <para>Every column is a nullable <c>string</c>, matching what
    /// <see cref="MappedDestinationParquetSerializer"/> actually writes: mapped values arrive at a destination
    /// already stringly-typed, so claiming a narrower type here would be a lie the Parquet does not back up, and
    /// a reader that trusted it would fail on the first non-conforming value.</para>
    /// </summary>
    internal static string BuildSchemaString(IReadOnlyList<string> columns)
    {
        var fields = new JsonArray();
        foreach (var column in columns)
        {
            fields.Add(new JsonObject
            {
                ["name"] = column,
                ["type"] = "string",
                ["nullable"] = true,
                ["metadata"] = new JsonObject(),
            });
        }

        return JsonSerializer.Serialize(new JsonObject
        {
            ["type"] = "struct",
            ["fields"] = fields,
        });
    }

    /// <summary>
    /// One action per line — the log is newline-delimited JSON, so an action must never be pretty-printed across
    /// lines.
    /// </summary>
    private static void AppendAction(StringBuilder builder, string actionName, JsonObject action)
    {
        var wrapper = new JsonObject { [actionName] = action };
        builder.Append(JsonSerializer.Serialize(wrapper)).Append('\n');
    }
}

/// <summary>One Parquet file added to a Delta table by a commit.</summary>
/// <param name="RelativePath">Path relative to the table root, as it appears in the add action.</param>
/// <param name="SizeBytes">Size of the written file.</param>
/// <param name="RecordCount">Rows in the file, reported as the add action's <c>numRecords</c> statistic.</param>
internal sealed record DeltaAddedFile(string RelativePath, long SizeBytes, long RecordCount);
