using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// One Fabric landing surface. "Fabric as a destination" is not a single target: OneLake Files speaks the blob
/// protocol against a storage audience, a Warehouse speaks TDS against <c>database.windows.net</c>, and a Delta
/// table needs a transaction log neither of those produces. Each is a strategy here, resolved through
/// <see cref="IFabricLandingStrategyRegistry"/>, so adding a surface is a new class plus one DI registration
/// rather than a branch inside the writer — the same registry/strategy shape
/// <c>ISourceApplicationStrategyRegistry</c> uses for the application-type axis.
/// </summary>
public interface IFabricLandingStrategy
{
    /// <summary>The landing mode this strategy implements. One strategy per mode; duplicates fail at startup.</summary>
    FabricLandingMode Handles { get; }

    /// <summary>
    /// Fabric item types this surface can actually write to, matched case-insensitively. Checked before the write
    /// so an impossible pairing (a Warehouse COPY INTO aimed at a Lakehouse, say) is refused with a message naming
    /// both halves, rather than failing deep inside a protocol call.
    /// </summary>
    IReadOnlySet<string> SupportedItemTypes { get; }

    Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        FabricDestinationSettings settings,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken);
}
