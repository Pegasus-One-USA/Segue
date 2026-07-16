using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Delivery;

/// <summary>
/// Persists the generated file and returns a signed, time-limited download URL — for exports too large or
/// long-running to hold in an HTTP response. Expiry comes from the destination's <c>dest_downloadLinkExpiryMinutes</c>
/// metadata field (default 60 minutes).
/// </summary>
public sealed class DownloadUrlDeliveryStrategy : IArtifactDeliveryStrategy
{
    private const int DefaultExpiryMinutes = 60;

    private readonly IGeneratedFileDownloadLinkService _downloadLinkService;

    public DownloadUrlDeliveryStrategy(IGeneratedFileDownloadLinkService downloadLinkService)
    {
        _downloadLinkService = downloadLinkService;
    }

    public async Task<DestinationWriteResult> DeliverAsync(
        DestinationConfiguration destination,
        GeneratedFile file,
        int recordCount,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var expiryMinutes = ConnectionMetadataReader.GetInt(
            destination.ConnectionMetadataJson, "dest_downloadLinkExpiryMinutes", DefaultExpiryMinutes);

        var url = await _downloadLinkService.CreateLinkAsync(
            file, TimeSpan.FromMinutes(expiryMinutes), cancellationToken);

        return new DestinationWriteResult(recordCount, DownloadUrl: url);
    }
}
