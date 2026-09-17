using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IAllowedCorsOriginRepository
{
    Task<IReadOnlyList<AllowedCorsOrigin>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Server-side paged/searchable read for the Allowed Origins screen. The CORS policy itself
    /// still uses <see cref="GetAllAsync"/> — paging is a UI concern only and must never narrow the set of
    /// origins the API actually allows.</summary>
    Task<PagedResult<AllowedCorsOrigin>> GetPagedAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<AllowedCorsOrigin?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string originUrl, CancellationToken cancellationToken);

    Task AddAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken);

    Task UpdateAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken);

    Task DeleteAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken);
}
