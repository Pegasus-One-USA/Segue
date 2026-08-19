using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IDeIdentificationProfileRepository
{
    Task<IReadOnlyList<DeIdentificationProfile>> ListAsync(CancellationToken cancellationToken);

    Task<DeIdentificationProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(DeIdentificationProfile profile, CancellationToken cancellationToken);
}
