using System.IO.Compression;
using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// First-class writer for the <c>Csv</c> destination. Groups the mapped records by FHIR resource type — each
/// resource type has its own, differently-shaped mapped column set, so combining them into one CSV would produce a
/// nonsensical merged table — and serializes only the mapped fields (no internal lineage columns) per resource type.
/// A single selected resource is delivered as one plain CSV; more than one is bundled into a single ZIP archive, one
/// CSV entry per resource type. Either way, delegates to the <see cref="ArtifactDeliveryMode"/>-keyed delivery
/// strategy resolved from <c>dest_deliveryMode</c> in <see cref="DestinationConfiguration.ConnectionMetadataJson"/>
/// (Download/Email/SFTP/Download-link) — this writer only owns CSV/ZIP serialization, not delivery.
/// </summary>
public sealed class MappedCsvDestinationWriter : IConfiguredDestinationWriter
{
    private readonly IArtifactDeliveryStrategyFactory _deliveryStrategyFactory;

    public MappedCsvDestinationWriter(IArtifactDeliveryStrategyFactory deliveryStrategyFactory)
    {
        _deliveryStrategyFactory = deliveryStrategyFactory;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var timestamp = DateTime.UtcNow;
        var groups = records
            .GroupBy(record => record.ResourceType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A single selected resource needs no ZIP wrapper — deliver its CSV directly, named after the resource type
        // itself. Only two or more resource types are bundled into one ZIP archive (named after the workflow, since
        // no single resource type would otherwise describe the whole export).
        var file = groups.Count == 1
            ? BuildSingleCsvFile(groups[0], timestamp)
            : BuildZipFile(groups, context.RouteName, timestamp);

        var deliveryMode = ParseDeliveryMode(destination.ConnectionMetadataJson);
        var strategy = _deliveryStrategyFactory.Create(deliveryMode);

        return await strategy.DeliverAsync(destination, file, records.Count, context, cancellationToken);
    }

    private static GeneratedFile BuildSingleCsvFile(IGrouping<string, MappedDestinationRecord> group, DateTime timestamp)
    {
        var csvBytes = Encoding.UTF8.GetBytes(MappedDestinationSerialization.ToMappedOnlyCsv([.. group]));
        return new GeneratedFile(BuildCsvEntryName(group.Key, timestamp), "text/csv", csvBytes);
    }

    /// <summary>
    /// One ZIP entry per resource type, named <c>{ResourceType}_{yyyyMMdd_HHmmss}.csv</c> (e.g.
    /// "Patient_20260721_143022.csv") — every entry shares the same <paramref name="timestamp"/>, so every file
    /// produced by one export run is trivially identifiable as a set. The archive itself is named
    /// <c>{WorkflowName}_{yyyyMMdd_HHmmss}.zip</c>.
    /// </summary>
    private static GeneratedFile BuildZipFile(
        IReadOnlyList<IGrouping<string, MappedDestinationRecord>> groups, string workflowName, DateTime timestamp)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var group in groups)
            {
                var entry = archive.CreateEntry(BuildCsvEntryName(group.Key, timestamp), CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                var csvBytes = Encoding.UTF8.GetBytes(MappedDestinationSerialization.ToMappedOnlyCsv([.. group]));
                entryStream.Write(csvBytes);
            }
        }

        var zipFileName = $"{CleanFileNamePart(workflowName)}_{timestamp:yyyyMMdd_HHmmss}.zip";
        return new GeneratedFile(zipFileName, "application/zip", buffer.ToArray());
    }

    private static string BuildCsvEntryName(string resourceType, DateTime timestamp) =>
        $"{CleanFileNamePart(resourceType)}_{timestamp:yyyyMMdd_HHmmss}.csv";

    private static string CleanFileNamePart(string value)
    {
        var cleaned = string.Join("_", value.Split(Path.GetInvalidFileNameChars().Append(' ').ToArray(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "Export" : cleaned;
    }

    private static ArtifactDeliveryMode ParseDeliveryMode(string? connectionMetadataJson)
    {
        var raw = ConnectionMetadataReader.GetString(connectionMetadataJson, "dest_deliveryMode");

        return raw?.Trim().ToLowerInvariant() switch
        {
            "email" => ArtifactDeliveryMode.Email,
            "sftp" => ArtifactDeliveryMode.Sftp,
            "downloadurl" => ArtifactDeliveryMode.DownloadUrl,
            _ => ArtifactDeliveryMode.Download
        };
    }
}
