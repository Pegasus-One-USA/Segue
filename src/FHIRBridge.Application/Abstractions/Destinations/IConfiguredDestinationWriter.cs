using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IConfiguredDestinationWriter
{
    Task<int> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken);
}
