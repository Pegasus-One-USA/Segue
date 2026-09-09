using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

public interface IEhrEndpointService
{
    Task<IReadOnlyList<EhrEndpointDto>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Server-side paged/filtered listing backing the admin EHR Endpoints screen — see
    /// <see cref="Abstractions.Persistence.IEhrEndpointRepository.GetPagedAsync"/> and
    /// <see cref="EhrEndpointFilter"/>.</summary>
    Task<PagedResult<EhrEndpointDto>> GetPagedAsync(
        EhrEndpointFilter filter, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Anonymous-safe listing/search of EhrEndpoint rows for one audience, scoped by
    /// <paramref name="endpointType"/> — backs the public ehr-public-endpoints controller. See
    /// <see cref="PublicEhrEndpointDto"/> for why this is a separate, narrower shape. <paramref name="search"/> is
    /// an optional case-insensitive contains-match on Name.</summary>
    Task<IReadOnlyList<PublicEhrEndpointDto>> GetPublicEndpointsAsync(
        EhrEndpointType endpointType, string? search, CancellationToken cancellationToken);

    Task<EhrEndpointDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="ehrEndpointId"/> resolves to an EhrEndpoint row of the given
    /// <paramref name="endpointType"/> — validates a request-time id came from the same audience-scoped set
    /// <see cref="GetPublicEndpointsAsync"/> exposes for that type, so a Patient-flow caller can't sneak in an
    /// Epic-sandbox row (or vice versa).</summary>
    Task<bool> IsKnownEndpointAsync(Guid ehrEndpointId, EhrEndpointType endpointType, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="ehrEndpointId"/> resolves to an EhrEndpoint row whose type is one of
    /// <paramref name="allowedTypes"/> — the multi-type form for an audience that spans more than one endpoint type
    /// (Provider Standalone accepts both <see cref="EhrEndpointType.Epic"/> and <see cref="EhrEndpointType.Ecw"/>
    /// vendor-sandbox rows). Patient Standalone keeps the single-type overload above.</summary>
    Task<bool> IsKnownEndpointAsync(Guid ehrEndpointId, IReadOnlyCollection<EhrEndpointType> allowedTypes, CancellationToken cancellationToken);

    Task<EhrEndpointDto> AddAsync(CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task<EhrEndpointDto> UpdateAsync(Guid id, CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
