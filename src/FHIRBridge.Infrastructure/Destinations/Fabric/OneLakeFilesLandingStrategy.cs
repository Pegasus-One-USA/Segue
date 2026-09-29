using System.Globalization;
using System.Text;
using Azure.Storage.Blobs.Models;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Lands mapped records as files in a Lakehouse's <c>Files/</c> (unmanaged) area over the OneLake endpoint. The
/// original — and still the default — Fabric landing surface; see <see cref="IOneLakeClientFactory"/> for the
/// Entra-only auth dispatch and <see cref="FabricDestinationSettings"/> for workspace/item addressing.
///
/// Two design points are worth stating plainly, because both are limits a reader will otherwise assume away:
/// <list type="number">
/// <item><description><b>Files, not tables.</b> A Fabric Lakehouse table is a Delta table, defined by its
/// <c>_delta_log</c>. This strategy emits ordinary Parquet/NDJSON/CSV, which is not a Delta table, so it writes to
/// the Files area and <see cref="FabricDestinationSettings.NormalizeBasePath"/> refuses a <c>Tables/</c> path
/// outright rather than producing an "unidentified area" the customer's table never appears in. To land a real
/// table, use <see cref="FabricLandingMode.LakehouseTable"/> (Delta) or
/// <see cref="FabricLandingMode.WarehouseTable"/> (COPY INTO) instead.</description></item>
/// <item><description><b>Append-only, one file per batch.</b> Object storage has no update-in-place, so each write
/// produces a new timestamped file — the same "write-once stream" shape
/// <c>MappedBlobStorageDestinationWriter</c>'s bulk mode produces. There is deliberately no Upsert/Update record
/// mode here: a mapping's upsert key means nothing to a file drop, and offering the setting would imply a
/// de-duplication guarantee this surface cannot make. De-duplication belongs downstream, in the Delta MERGE or
/// view that consumes these files.</description></item>
/// </list>
/// </summary>
internal sealed class OneLakeFilesLandingStrategy : IFabricLandingStrategy
{
    private readonly IOneLakeClientFactory _clientFactory;
    private readonly ILogger<OneLakeFilesLandingStrategy> _logger;

    public OneLakeFilesLandingStrategy(
        IOneLakeClientFactory clientFactory, ILogger<OneLakeFilesLandingStrategy> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public FabricLandingMode Handles => FabricLandingMode.OneLakeFiles;

    /// <summary>
    /// Lakehouse only. A Warehouse has no <c>Files/</c> area of its own to drop into — staging for a Warehouse
    /// load goes to a Lakehouse, which is what <see cref="WarehouseTableLandingStrategy"/> does.
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
        // Resolving the workspace is where this destination's Entra credential is built and the OneLake item
        // addressed, so an unusable identity or an unresolvable workspace/item fails here rather than mid-upload.
        var workspace = await context.ReportConnectAsync(
            () => _clientFactory.GetWorkspaceAsync(destination, settings, cancellationToken),
            cancellationToken,
            detail: "OneLake");

        var (payload, contentType) = await SerializeAsync(settings, records, cancellationToken);
        var filePath = BuildFilePath(settings, mappingProfile, DateTime.UtcNow);
        var blobClient = workspace.Container.GetBlobClient(filePath);

        using var stream = new MemoryStream(payload);
        await blobClient.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Metadata = BuildMetadata(mappingProfile, records, context),
                // No AccessTier: OneLake has no storage-tier concept, and sending one gets rejected rather than
                // silently ignored.
            },
            cancellationToken);

        _logger.LogInformation(
            "Wrote {RecordCount} {ResourceType} record(s) to OneLake {Workspace}/{FilePath} for destination {DestinationId}.",
            records.Count,
            mappingProfile.ResourceType,
            settings.Workspace,
            filePath,
            destination.Id);

        return new DestinationWriteResult(records.Count);
    }

    private static async Task<(byte[] Payload, string ContentType)> SerializeAsync(
        FabricDestinationSettings settings,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        if (settings.FileFormat == FabricFileFormat.Parquet)
        {
            return (await MappedDestinationParquetSerializer.SerializeAsync(records, cancellationToken), settings.ContentType);
        }

        var text = settings.FileFormat == FabricFileFormat.Csv
            ? MappedDestinationSerialization.ToCsv(records)
            // LF-joined, not Environment.NewLine: a Spark/Fabric reader parsing line-delimited JSON expects LF, and
            // CRLF is exactly what makes a Windows-produced NDJSON feed fail to parse there.
            : string.Join('\n', records.Select(MappedDestinationSerialization.ToJson));

        return (Encoding.UTF8.GetBytes(text), settings.ContentType);
    }

    /// <summary>
    /// Builds the full blob path inside the workspace container:
    /// <c>{item}.Lakehouse/Files/{basePath}[/resourceType=X][/ingest_date=Y]/{object}_{timestamp}.{ext}</c>.
    /// The partition folders use Hive-style <c>key=value</c> naming because that is what Spark, a Fabric shortcut
    /// and a Warehouse <c>OPENROWSET</c> all recognize as a partition column automatically — a plain <c>Patient/</c>
    /// folder is just a folder and the resource type is then invisible to a query.
    /// </summary>
    internal static string BuildFilePath(
        FabricDestinationSettings settings, MappingProfile mappingProfile, DateTime timestampUtc)
    {
        var segments = new List<string> { settings.RootPath };

        if (settings.Partitioning is FabricPartitionScheme.ResourceType or FabricPartitionScheme.ResourceTypeAndIngestDate)
        {
            segments.Add($"resourceType={Sanitize(mappingProfile.ResourceType)}");
        }

        if (settings.Partitioning is FabricPartitionScheme.IngestDate or FabricPartitionScheme.ResourceTypeAndIngestDate)
        {
            segments.Add($"ingest_date={timestampUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        var stem = Sanitize(StripKnownExtension(StripWriteModeSuffix(mappingProfile.DestinationObject)));
        if (stem.Length == 0)
        {
            stem = Sanitize(mappingProfile.ResourceType);
        }

        segments.Add(
            $"{stem}_{timestampUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}.{settings.FileExtension}");

        return string.Join('/', segments);
    }

    /// <summary>
    /// Defense in depth against a <c>;mode=...</c> write-mode suffix reaching here. That convention means something
    /// only to <c>MappedSqlServerDestinationWriter</c>'s target parsing, and the Runtime executor already
    /// skips appending it for file-shaped destinations — but a stray one would otherwise end up in a file name, the
    /// same way <c>MappedBlobStorageDestinationWriter</c> guards against it.
    /// </summary>
    private static string StripWriteModeSuffix(string destinationObject)
    {
        var suffixIndex = destinationObject.IndexOf(';', StringComparison.Ordinal);
        return suffixIndex < 0 ? destinationObject : destinationObject[..suffixIndex];
    }

    // A destination object saved through a CSV-shaped wizard step can carry an extension already ("patients.csv");
    // stripping it avoids a double-extensioned file such as "patients.csv_20260908120000000.parquet".
    private static readonly string[] KnownStemExtensions = [".ndjson", ".json", ".csv", ".tsv", ".txt", ".parquet"];

    private static string StripKnownExtension(string stem)
    {
        foreach (var extension in KnownStemExtensions)
        {
            if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^extension.Length];
            }
        }

        return stem;
    }

    private static string Sanitize(string value)
        => string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    private static Dictionary<string, string> BuildMetadata(
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context)
        => new()
        {
            ["pipelineRunId"] = records.First().PipelineRunId.ToString(),
            ["resourceType"] = mappingProfile.ResourceType,
            ["destinationObject"] = mappingProfile.DestinationObject,
            ["recordCount"] = records.Count.ToString(CultureInfo.InvariantCulture),
            ["writtenOnUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            // Blob metadata values must be header-safe ASCII; a user-typed workflow name is not guaranteed to be.
            ["routeName"] = SanitizeAscii(context.RouteName),
        };

    private static string SanitizeAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is >= (char)32 and < (char)127 ? character : '_');
        }

        return builder.ToString();
    }
}
