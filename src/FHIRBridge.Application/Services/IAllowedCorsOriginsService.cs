using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IAllowedCorsOriginsService
{
    Task<IReadOnlyList<AllowedCorsOriginDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<AllowedCorsOriginDto> AddAsync(CreateAllowedCorsOriginRequest request, CancellationToken cancellationToken);

    Task<AllowedCorsOriginDto> UpdateAsync(Guid id, UpdateAllowedCorsOriginRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Manually forces every replica to pick up the current AllowedCorsOrigins rows immediately, rather
    /// than waiting out the cache's own staleness bound — see InProcessAllowedCorsOriginsCache's remarks.
    /// An admin edit already triggers this on its own; this exists for an explicit "reload now" action.
    /// </summary>
    void Reload();
}
