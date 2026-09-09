using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.Blob;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Resolves a Fabric destination's credential and builds the workspace-scoped client the writer uploads through.
/// The seam that lets <see cref="MappedDataFabricDestinationWriter"/> be unit-tested without a live Fabric tenant —
/// the same role <see cref="IBlobContainerClientFactory"/> plays for the Blob Storage writer.
/// </summary>
public interface IOneLakeClientFactory
{
    /// <summary>
    /// The returned target's container is the OneLake WORKSPACE (not a storage container), and its
    /// <see cref="BlobDestinationTarget.SupportsContainerCreate"/> is always false: a Fabric workspace is created in
    /// Fabric, never by a storage-protocol call, so attempting one would only produce a confusing 403.
    /// </summary>
    Task<BlobDestinationTarget> GetWorkspaceAsync(
        DestinationConfiguration destination,
        FabricDestinationSettings settings,
        CancellationToken cancellationToken);
}
