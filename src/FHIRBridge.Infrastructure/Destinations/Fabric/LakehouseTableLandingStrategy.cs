using System.Globalization;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Lands mapped records as rows in a Lakehouse <c>Tables/</c> Delta table: one Parquet file per batch, plus the
/// <c>_delta_log</c> commit that makes those files a table.
///
/// <para><b>Why this exists alongside OneLake Files.</b> <see cref="FabricLandingMode.OneLakeFiles"/> writes the
/// same Parquet but no log, so Fabric sees a folder rather than a table and the customer has to promote it by hand
/// with a shortcut or a notebook. The transaction log is the entire difference, and it is why
/// <see cref="FabricDestinationSettings.NormalizeBasePath"/> refuses a <c>Tables/</c> path for that mode.</para>
///
/// <para><b>Why not the Warehouse strategy.</b> <see cref="FabricLandingMode.WarehouseTable"/> reaches a Warehouse
/// over TDS and lets the engine do the load. A Lakehouse has no writable SQL endpoint — its SQL analytics endpoint
/// is read-only — so rows can only arrive by writing the table's files directly, which means speaking Delta.</para>
///
/// <para><b>Append-only.</b> See <see cref="DeltaTransactionLog"/>: this commits <c>add</c> actions only, so
/// Upsert is not offered here. A destination configured for Upsert is refused at write time with a message
/// pointing at the Warehouse surface, rather than silently appending and leaving duplicate rows behind — which is
/// the failure a customer would otherwise discover much later, in their own reporting.</para>
///
/// <para><b>Unverified against a live Fabric tenant.</b> The log follows the Delta protocol specification, but no
/// table written by this code has been opened in Fabric yet. If a table does not register, check in this order:
/// (1) the table folder is directly under <c>Tables/</c> — Fabric does not discover a table nested in a subfolder
/// unless the Lakehouse is schema-enabled, in which case it is <c>Tables/{schema}/{table}</c>; (2) the commit file
/// is exactly <c>_delta_log/00000000000000000000.json</c>, zero-padded to 20 digits; (3) <c>schemaString</c> is a
/// JSON <i>string</i> rather than a nested object, which is the easiest field in the protocol to get wrong.</para>
/// </summary>
internal sealed class LakehouseTableLandingStrategy : IFabricLandingStrategy
{
    /// <summary>
    /// How many times a losing commit retries. Each attempt re-reads the table's current version, so a retry is a
    /// fresh commit at the next free version rather than a replay of the same one. Small on purpose: contention
    /// here means several pipelines writing one table concurrently, and failing with a clear message beats
    /// retrying long enough to look like a hang.
    /// </summary>
    private const int MaxCommitAttempts = 5;

    private readonly IOneLakeClientFactory _clientFactory;
    private readonly ILogger<LakehouseTableLandingStrategy> _logger;

    public LakehouseTableLandingStrategy(
        IOneLakeClientFactory clientFactory, ILogger<LakehouseTableLandingStrategy> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public FabricLandingMode Handles => FabricLandingMode.LakehouseTable;

    /// <summary>
    /// Lakehouse only. A Warehouse's tables live in its own storage and are loaded through the engine, not by
    /// writing Delta files at them — that is <see cref="WarehouseTableLandingStrategy"/>.
    /// </summary>
    public IReadOnlySet<string> SupportedItemTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Lakehouse" };

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        FabricDestinationSettings settings,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (settings.WarehouseWriteMode == FabricTableWriteMode.Upsert)
        {
            // Refused rather than silently downgraded to append: a customer who configured Upsert expects matched
            // rows to be replaced, and appending instead produces duplicates they would find much later in their
            // own reports. See DeltaTransactionLog on what upsert would actually require.
            throw new NotSupportedException(
                $"Destination '{destination.Name}': Upsert is not supported when landing in a Lakehouse Delta "
                    + "table — this surface appends new files and never rewrites existing ones. Use Append here, or "
                    + "use a Fabric Warehouse destination, whose load runs a MERGE inside the warehouse engine.");
        }

        // Mapped columns only, exactly as the Warehouse strategy selects them: the lineage columns GetColumns
        // prepends are FHIRBridge's own and have no place in a table whose schema is the customer's mapping.
        var columns = MappedDestinationSerialization.GetMappedColumns(records);
        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': mapping profile '{mappingProfile.Name}' produced no columns, so "
                    + "there is nothing to write to the Lakehouse table.");
        }

        var workspace = await context.ReportConnectAsync(
            () => _clientFactory.GetWorkspaceAsync(destination, settings, cancellationToken),
            cancellationToken,
            detail: "OneLake");

        var tableRoot = settings.LakehouseTableRootPath(ResolveTableName(settings, mappingProfile));
        var timestamp = DateTimeOffset.UtcNow;

        // Data file first, commit second. A data file with no commit referencing it is invisible to every Delta
        // reader — wasted space, but not corruption — whereas a commit naming a file that does not exist breaks
        // the table for everyone reading it. So the order is deliberate, and it is why a failed commit below does
        // not attempt to delete what it already wrote.
        var dataFileName = BuildDataFileName(mappingProfile, timestamp);
        var payload = await MappedDestinationParquetSerializer.SerializeAsync(records, columns, cancellationToken);

        var dataBlob = workspace.Container.GetBlobClient($"{tableRoot}/{dataFileName}");
        using (var stream = new MemoryStream(payload))
        {
            await dataBlob.UploadAsync(stream, overwrite: true, cancellationToken);
        }

        var addedFile = new DeltaAddedFile(dataFileName, payload.LongLength, records.Count);
        var version = await CommitAsync(
            workspace.Container, tableRoot, settings, mappingProfile, columns, addedFile, timestamp, destination,
            cancellationToken);

        _logger.LogInformation(
            "Wrote {RecordCount} {ResourceType} record(s) to Fabric Lakehouse Delta table {TableRoot} at version "
                + "{Version} for destination {DestinationId}.",
            records.Count,
            mappingProfile.ResourceType,
            tableRoot,
            version,
            destination.Id);

        return new DestinationWriteResult(records.Count);
    }

    /// <summary>
    /// Commits the new data file at the next free version, retrying when another writer takes that version first.
    ///
    /// <para>Delta's concurrency control is the destination refusing to overwrite an existing commit file, so the
    /// upload below is conditional (<c>If-None-Match: *</c>): two writers racing for version N produce one winner
    /// and one <c>409 BlobAlreadyExists</c>. An unconditional write would let the loser silently destroy the
    /// winner's commit, which is the single worst thing this class could do to a customer's table — hence the
    /// condition rather than a check-then-write, which has a race between the two halves.</para>
    /// </summary>
    private async Task<long> CommitAsync(
        BlobContainerClient container,
        string tableRoot,
        FabricDestinationSettings settings,
        MappingProfile mappingProfile,
        IReadOnlyList<string> columns,
        DeltaAddedFile addedFile,
        DateTimeOffset timestamp,
        DestinationConfiguration destination,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxCommitAttempts; attempt++)
        {
            var nextVersion = await GetNextVersionAsync(container, tableRoot, cancellationToken);

            // Version 0 is the table's creation, and only it carries protocol and metaData actions.
            var commit = nextVersion == 0
                ? DeltaTransactionLog.BuildInitialCommit(
                    DeriveTableId(destination, tableRoot),
                    ResolveTableName(settings, mappingProfile),
                    columns,
                    [addedFile],
                    timestamp)
                : DeltaTransactionLog.BuildAppendCommit([addedFile], timestamp);

            var commitBlob = container.GetBlobClient(
                $"{tableRoot}/_delta_log/{DeltaTransactionLog.CommitFileName(nextVersion)}");

            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(commit));
                await commitBlob.UploadAsync(
                    stream,
                    new BlobUploadOptions
                    {
                        // "*" means "only if no blob exists at this path" — the conditional that makes the commit
                        // atomic against a concurrent writer.
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    },
                    cancellationToken);

                return nextVersion;
            }
            catch (RequestFailedException exception) when (exception.Status == 409)
            {
                // Another writer committed this version between the listing and the upload. Re-read and try the
                // next one; the data file already written stays valid and is named by whichever commit succeeds.
                _logger.LogDebug(
                    "Delta commit for version {Version} of {TableRoot} lost a race (attempt {Attempt} of "
                        + "{MaxAttempts}); retrying at the next version.",
                    nextVersion,
                    tableRoot,
                    attempt,
                    MaxCommitAttempts);
            }
        }

        throw new InvalidOperationException(
            $"Destination '{destination.Name}': could not commit to the Lakehouse Delta table at {tableRoot} after "
                + $"{MaxCommitAttempts} attempts — another writer took each version first. The data file was "
                + "written but is not yet part of the table. Reduce concurrent writes to this table, or stagger "
                + "the schedules that target it.");
    }

    /// <summary>
    /// The next free commit version: one past the highest numbered commit file, or 0 for a table that does not
    /// exist yet.
    ///
    /// <para>Reads the log by listing rather than by parsing <c>_last_checkpoint</c>. Listing is correct whether
    /// or not a checkpoint exists, and an append-only writer never needs the reconstructed table state a
    /// checkpoint accelerates — only the highest version number, which the file names carry. It does mean the
    /// listing grows with the table's commit count; a table taking so many commits that this matters wants
    /// checkpointing, which is noted as future work rather than pretended at here.</para>
    /// </summary>
    private static async Task<long> GetNextVersionAsync(
        BlobContainerClient container, string tableRoot, CancellationToken cancellationToken)
    {
        var highest = -1L;
        var prefix = $"{tableRoot}/_delta_log/";

        await foreach (var blob in container.GetBlobsAsync(
            prefix: prefix, cancellationToken: cancellationToken))
        {
            var name = blob.Name[prefix.Length..];

            // Commit files only: a checkpoint (".checkpoint.parquet") or _last_checkpoint shares the folder, and
            // neither names a version this writer may take.
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (long.TryParse(
                    name[..^".json".Length],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var version)
                && version > highest)
            {
                highest = version;
            }
        }

        return highest + 1;
    }

    /// <summary>
    /// A table's Delta id, stable for a given destination and table path.
    ///
    /// <para>Deterministic rather than <see cref="Guid.NewGuid"/> so that re-creating a table that was deleted
    /// outside FHIRBridge produces the same id it had before, rather than a new identity for what the customer
    /// considers the same table. Derived from the destination id and the table root, which is exactly the pair
    /// that identifies it.</para>
    /// </summary>
    internal static Guid DeriveTableId(DestinationConfiguration destination, string tableRoot)
    {
        var seed = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"{destination.Id}|{tableRoot}"));

        return new Guid(seed.AsSpan(0, 16));
    }

    /// <summary>
    /// The Delta table's name: the destination's explicit override, else the mapping's destination object with any
    /// schema qualifier and write-mode suffix removed.
    ///
    /// <para>A Lakehouse table name is a folder name, so "dbo.Patient" would create a folder literally called
    /// "dbo.Patient" rather than a Patient table in a dbo schema. Taking the last dot-separated segment matches
    /// what the Warehouse strategy does with the same input, so one mapping profile produces the same table name
    /// on either surface.</para>
    /// </summary>
    internal static string ResolveTableName(FabricDestinationSettings settings, MappingProfile mappingProfile)
    {
        if (!string.IsNullOrWhiteSpace(settings.WarehouseTable))
        {
            return Sanitize(settings.WarehouseTable);
        }

        var stem = mappingProfile.DestinationObject ?? string.Empty;

        // "dbo.Patient;mode=upsert" — the write-mode suffix is not part of the name.
        var suffixIndex = stem.IndexOf(';', StringComparison.Ordinal);
        if (suffixIndex >= 0)
        {
            stem = stem[..suffixIndex];
        }

        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length == 0 ? string.Empty : Sanitize(parts[^1]);

        return name.Length == 0 ? Sanitize(mappingProfile.ResourceType) : name;
    }

    /// <summary>
    /// One Parquet file per batch, named so that two concurrent writes cannot collide. The <c>part-</c> prefix is
    /// the convention every Delta and Spark writer follows; nothing reads it, but a customer browsing the folder
    /// sees what they expect from a Delta table rather than something that looks foreign.
    /// </summary>
    internal static string BuildDataFileName(MappingProfile mappingProfile, DateTimeOffset timestamp)
        => $"part-{timestamp.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.parquet";

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
