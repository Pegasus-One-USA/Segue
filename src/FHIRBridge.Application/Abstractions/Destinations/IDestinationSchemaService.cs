using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IDestinationSchemaService
{
    Task<DestinationSchemaDto> GetSchemaAsync(Guid destinationId, CancellationToken cancellationToken);

    /// <summary>
    /// Tests an ad-hoc (unsaved) relational connection and, on success, returns its tables/columns — powers the
    /// builder's "test connection → pick table/column" flow. Never throws for connection failures; returns
    /// <c>Connected=false</c> + <c>Error</c> instead.
    /// </summary>
    Task<DestinationSchemaProbeDto> ProbeSchemaAsync(
        DestinationConnectionProbeRequest request,
        CancellationToken cancellationToken);
}
