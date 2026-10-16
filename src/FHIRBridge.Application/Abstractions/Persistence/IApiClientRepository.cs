using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IApiClientRepository
{
    Task<PagedResult<ApiClient>> GetPagedAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<ApiClient?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<ApiClient?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken);

    Task<bool> ReturnUrlExistsAsync(Guid apiClientId, string url, CancellationToken cancellationToken);

    Task AddAsync(ApiClient client, CancellationToken cancellationToken);

    Task UpdateAsync(ApiClient client, CancellationToken cancellationToken);

    Task DeleteAsync(ApiClient client, CancellationToken cancellationToken);
}
