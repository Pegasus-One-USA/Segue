using Azure.Storage.Blobs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// Resolves a destination's secret (when its auth mode needs one) and builds the right <see cref="BlobContainerClient"/>
/// for it. The seam that lets <see cref="MappedBlobStorageDestinationWriter"/> and
/// <see cref="BlobStorageDestinationHealthCheckProvider"/> be unit-tested without a real Azure Storage account —
/// tests substitute a fake/mock implementation of this interface.
/// </summary>
public interface IBlobContainerClientFactory
{
    Task<BlobDestinationTarget> GetTargetAsync(
        DestinationConfiguration destination,
        BlobDestinationSettings settings,
        CancellationToken cancellationToken);
}

/// <param name="Container">The container-scoped client to read/write against.</param>
/// <param name="SupportsContainerCreate">
/// False when the client was built from a container-scoped SAS: that SAS has no permission over the account
/// itself, so a <c>CreateIfNotExistsAsync</c> call would 403 on every write. True for every other auth shape.
/// </param>
public sealed record BlobDestinationTarget(BlobContainerClient Container, bool SupportsContainerCreate);
