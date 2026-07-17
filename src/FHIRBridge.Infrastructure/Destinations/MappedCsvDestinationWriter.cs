using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// First-class writer for the <c>Csv</c> destination. Serializes the mapped records to CSV once, then delegates to
/// the <see cref="ArtifactDeliveryMode"/>-keyed delivery strategy resolved from <c>dest_deliveryMode</c> in
/// <see cref="DestinationConfiguration.ConnectionMetadataJson"/> (Download/Email/SFTP/Download-link) — this writer
/// only owns CSV serialization, not delivery.
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

        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "csv");
        var csv = MappedDestinationSerialization.ToCsv(records);
        var file = new GeneratedFile(fileName, "text/csv", Encoding.UTF8.GetBytes(csv));

        var deliveryMode = ParseDeliveryMode(destination.ConnectionMetadataJson);
        var strategy = _deliveryStrategyFactory.Create(deliveryMode);

        return await strategy.DeliverAsync(destination, file, records.Count, context, cancellationToken);
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
