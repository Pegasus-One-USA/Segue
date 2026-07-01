using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IDestinationSchemaService
{
    Task<DestinationSchemaDto> GetSchemaAsync(Guid tenantId, Guid destinationId, CancellationToken cancellationToken);
}
