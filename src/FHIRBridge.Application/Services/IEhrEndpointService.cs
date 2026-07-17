using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IEhrEndpointService
{
    Task<IReadOnlyList<EhrEndpointDto>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Anonymous-safe listing/search of Epic-sandbox-typed endpoints only — backs the public
    /// ehr-epic-endpoints controller. See <see cref="PublicEhrEpicEndpointDto"/> for why this is a separate,
    /// narrower shape. <paramref name="search"/> is an optional case-insensitive contains-match on Name.</summary>
    Task<IReadOnlyList<PublicEhrEpicEndpointDto>> GetPublicEpicEndpointsAsync(
        string? search, CancellationToken cancellationToken);

    Task<EhrEndpointDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="ehrEndpointId"/> resolves to an EndpointType.Epic row — validates a
    /// request-time id came from the same restricted set <see cref="GetPublicEpicEndpointsAsync"/> exposes, not an
    /// arbitrary/other-typed (e.g. a specific customer's live MyChart) row.</summary>
    Task<bool> IsEpicEndpointAsync(Guid ehrEndpointId, CancellationToken cancellationToken);

    Task<EhrEndpointDto> AddAsync(CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task<EhrEndpointDto> UpdateAsync(Guid id, CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
