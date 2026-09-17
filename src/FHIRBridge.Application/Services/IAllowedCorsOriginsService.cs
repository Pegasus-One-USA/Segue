using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IAllowedCorsOriginsService
{
    Task<IReadOnlyList<AllowedCorsOriginDto>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Server-side paged/searchable read backing the Allowed Origins screen.</summary>
    Task<PagedResult<AllowedCorsOriginDto>> GetPagedAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<AllowedCorsOriginDto> AddAsync(CreateAllowedCorsOriginRequest request, CancellationToken cancellationToken);

    Task<AllowedCorsOriginDto> UpdateAsync(Guid id, UpdateAllowedCorsOriginRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
