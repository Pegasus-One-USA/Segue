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

    /// <summary>Paged, search-filtered listing for the admin EHR Endpoints screen — the directory can be in the
    /// hundreds of rows (vendor-imported directories), so unlike <see cref="GetAllAsync"/> this filters/pages at
    /// the query level. <paramref name="search"/> is an optional case-insensitive contains-match on Name, Vendor, or
    /// FhirBaseUrl. <paramref name="sortDescending"/> orders by the "action on" timestamp (ModifiedOnUtc, falling
    /// back to CreatedOnUtc) — the only sortable column on that screen; null keeps the default Name ordering.</summary>
    Task<PagedResult<EhrEndpoint>> GetPagedAsync(
        string? search, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Filtered at the query level (not GetAllAsync + in-memory filter) — the directory can be in the
    /// hundreds of rows. <paramref name="endpointType"/> scopes the anonymous public listing to one audience (Epic
    /// sandbox rows for Provider Standalone, MyChart rows for Patient Standalone — see EhrPublicEndpointsController)
    /// so the two flows can never surface each other's rows. <paramref name="search"/>, when given, is a
    /// case-insensitive contains-match on Name.</summary>
    Task<IReadOnlyList<EhrEndpoint>> GetPublicAsync(
        EhrEndpointType endpointType, string? search, CancellationToken cancellationToken);

    Task<EhrEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);

    Task UpdateAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);

    Task DeleteAsync(EhrEndpoint endpoint, CancellationToken cancellationToken);
}
