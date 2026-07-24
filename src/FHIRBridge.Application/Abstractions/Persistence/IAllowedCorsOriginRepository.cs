using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IAllowedCorsOriginRepository
{
    Task<IReadOnlyList<AllowedCorsOrigin>> GetAllAsync(CancellationToken cancellationToken);

    Task<AllowedCorsOrigin?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string originUrl, CancellationToken cancellationToken);

    Task AddAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken);

    Task DeleteAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken);
}
