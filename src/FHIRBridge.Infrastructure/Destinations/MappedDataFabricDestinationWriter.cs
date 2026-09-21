using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.Fabric;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writer for the <c>DataFabricAzure</c> destination. "Fabric as a destination" is not one target — it is several
/// surfaces with different protocols, auth audiences and failure modes — so this writer resolves the configured
/// <see cref="FabricLandingMode"/> to an <see cref="IFabricLandingStrategy"/> and delegates. Adding a surface is a
/// new strategy plus one DI registration, never a branch here.
///
/// The one check this writer keeps for itself is the item-type/mode pairing, because it is the mismatch that would
/// otherwise fail deep inside a protocol call with a message naming neither half: a Warehouse COPY INTO aimed at a
/// Lakehouse, or a file drop aimed at a Warehouse that has no Files area.
/// </summary>
public sealed class MappedDataFabricDestinationWriter : IConfiguredDestinationWriter
{
    private readonly IFabricLandingStrategyRegistry _registry;

    public MappedDataFabricDestinationWriter(IFabricLandingStrategyRegistry registry)
    {
        _registry = registry;
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

        var settings = FabricDestinationSettings.Parse(destination);
        var strategy = _registry.Resolve(settings.Mode);

        if (!strategy.SupportedItemTypes.Contains(settings.ItemType))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': Fabric landing mode '{settings.Mode}' cannot write to item type "
                    + $"'{settings.ItemType}'. Supported for this mode: "
                    + string.Join(", ", strategy.SupportedItemTypes.Order()) + ".");
        }

        return await strategy.WriteAsync(
            destination, settings, mappingProfile, records, context, cancellationToken);
    }
}
