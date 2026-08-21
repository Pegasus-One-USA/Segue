using Azure;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// Blob-specific replacement for <see cref="TargetReachabilityDestinationHealthCheckProvider"/>'s generic
/// HTTP-HEAD-the-secret check, now that the blob secret can be a connection string, account key, or client
/// secret rather than always a URL. Confirms the container is reachable under whichever auth mode the
/// destination is configured for.
/// </summary>
public sealed class BlobStorageDestinationHealthCheckProvider : IDestinationHealthCheckProvider
{
    private readonly IBlobContainerClientFactory _clientFactory;

    public BlobStorageDestinationHealthCheckProvider(IBlobContainerClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public DestinationType DestinationType => DestinationType.BlobStorage;

    public async Task<DestinationHealthCheckResult> CheckAsync(
        DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        try
        {
            var settings = BlobDestinationSettings.Parse(destination);
            var target = await _clientFactory.GetTargetAsync(destination, settings, cancellationToken);
            var exists = await target.Container.ExistsAsync(cancellationToken);

            return new DestinationHealthCheckResult(
                exists.Value, exists.Value ? "Container reachable" : "Container not found");
        }
        catch (RequestFailedException exception)
        {
            return new DestinationHealthCheckResult(false, $"HTTP {exception.Status}: {exception.ErrorCode}");
        }
        catch (Exception exception)
        {
            return new DestinationHealthCheckResult(false, exception.Message);
        }
    }
}
