using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Access to the vendor EHR endpoint directory (<see cref="EhrEndpoint"/>). Rows are normally populated by
/// IEhrEndpointDirectorySeeder implementations, but admins can also add/edit/remove rows by hand (e.g. via the
/// portal) — a hand-edited row can be overwritten if its vendor's seeder later re-syncs the same VendorEndpointId.
/// </summary>
public interface IEhrEndpointRepository
{
    Task<IReadOnlyList<EhrEndpoint>> GetAllAsync(CancellationToken cancellationToken);

    Task<EhrEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);

    Task UpdateAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);

    Task DeleteAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);
}
