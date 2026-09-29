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

    /// <summary>
    /// Same client for a destination that has not been saved yet — the wizard's "test connection, then pick a
    /// table" flow, which runs before anything is provisioned. The service-principal secret is passed directly
    /// rather than resolved from Key Vault, because there is no stored secret reference to resolve. Mirrors
    /// <see cref="IFabricWarehouseConnectionFactory.OpenAdHocAsync"/>, which exists for the same reason.
    /// </summary>
    Task<BlobDestinationTarget> GetWorkspaceAdHocAsync(
        FabricDestinationSettings settings,
        string? secret,
        CancellationToken cancellationToken);
}
