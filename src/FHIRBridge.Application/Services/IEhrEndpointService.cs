using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IEhrEndpointService
{
    Task<IReadOnlyList<EhrEndpointDto>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Anonymous-safe listing/search of every EhrEndpoint row, regardless of EndpointType — backs the
    /// public ehr-public-endpoints controller. See <see cref="PublicEhrEndpointDto"/> for why this is a separate,
    /// narrower shape. <paramref name="search"/> is an optional case-insensitive contains-match on Name.</summary>
    Task<IReadOnlyList<PublicEhrEndpointDto>> GetPublicEndpointsAsync(
        string? search, CancellationToken cancellationToken);

    Task<EhrEndpointDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="ehrEndpointId"/> resolves to any EhrEndpoint row — validates a request-time
    /// id came from the same set <see cref="GetPublicEndpointsAsync"/> exposes, regardless of EndpointType.</summary>
    Task<bool> IsKnownEndpointAsync(Guid ehrEndpointId, CancellationToken cancellationToken);

    Task<EhrEndpointDto> AddAsync(CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task<EhrEndpointDto> UpdateAsync(Guid id, CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
