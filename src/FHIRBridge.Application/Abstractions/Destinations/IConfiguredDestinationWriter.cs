using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IConfiguredDestinationWriter
{
    Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken);
}
