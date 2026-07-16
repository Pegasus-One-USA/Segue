using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Access to the vendor EHR endpoint directory (<see cref="EhrEndpoint"/>). Rows are normally populated by
/// IEhrEndpointDirectorySeeder implementations, but admins can also add/edit/remove rows by hand (e.g. via the
/// portal) — a hand-edited row can be overwritten if its vendor's seeder later re-syncs the same VendorEndpointId.
/// </summary>
public interface IEhrEndpointRepository
{
    Task<IReadOnlyList<EhrEndpoint>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Filtered at the query level (not GetAllAsync + in-memory filter) — the MyChart directory alone is
    /// already in the hundreds of rows, and callers of this (e.g. the anonymous ehr-epic-endpoints listing) only
    /// ever want the handful of Epic-sandbox rows. <paramref name="search"/>, when given, is a case-insensitive
    /// contains-match on Name, applied in the same query (not an in-memory filter after the fact) so it still scales
    /// once this is pointed at a larger directory.</summary>
    Task<IReadOnlyList<EhrEndpoint>> GetByEndpointTypeAsync(
        EhrEndpointType endpointType, string? search, CancellationToken cancellationToken);

    Task<EhrEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);

    Task UpdateAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);

    Task DeleteAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);
}
