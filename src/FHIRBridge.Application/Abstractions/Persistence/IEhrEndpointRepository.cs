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

    /// <summary>Paged, filtered listing for the admin EHR Endpoints screen — the directory can be in the
    /// hundreds of rows (vendor-imported directories), so unlike <see cref="GetAllAsync"/> this filters/pages at
    /// the query level. See <see cref="EhrEndpointFilter"/> for the supported filters.
    /// <paramref name="sortDescending"/> orders by the "action on" timestamp (ModifiedOnUtc, falling
    /// back to CreatedOnUtc) — the only sortable column on that screen; null keeps the default Name ordering.</summary>
    Task<PagedResult<EhrEndpoint>> GetPagedAsync(
        EhrEndpointFilter filter, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Distinct vendors that actually have endpoint rows, for the listing's Source filter options.
    /// Deliberately unfiltered: the option list must not shrink as filters are applied, or picking a vendor would
    /// remove every other vendor from the dropdown it was picked from.</summary>
    Task<IReadOnlyList<SourceSystemType>> GetDistinctVendorsAsync(CancellationToken cancellationToken);

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

/// <summary>
/// Filters for the admin EHR Endpoints listing. All members are optional; a null/blank member is not applied.
/// </summary>
/// <param name="Search">Case-insensitive contains-match on Name or FhirBaseUrl.</param>
/// <param name="Vendor">Exact match on <see cref="EhrEndpoint.Vendor"/> — the screen's "Source" dropdown.</param>
/// <param name="IsActive">
/// true keeps only rows whose Status is "active", false only rows whose Status is anything else. Mirrors exactly
/// what the listing renders in its Status column, which is likewise a plain "active or not" split over the
/// free-text <see cref="EhrEndpoint.Status"/> string rather than a closed enum.
/// </param>
public sealed record EhrEndpointFilter(
    string? Search = null,
    SourceSystemType? Vendor = null,
    bool? IsActive = null);
