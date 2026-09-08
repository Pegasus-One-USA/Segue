using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfEhrEndpointRepository : IEhrEndpointRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfEhrEndpointRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    /// <summary>Lower-cased so the comparison itself can be case-insensitive on PostgreSQL too, whose <c>=</c>
    /// is case-sensitive unlike SQL Server's default collation — rows are seeded as "active" but the screen lets
    /// an admin hand-type the Status, so "Active" must count as active as well.</summary>
    private const string ActiveStatus = "active";

    public async Task<IReadOnlyList<EhrEndpoint>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.EhrEndpoints.OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<PagedResult<EhrEndpoint>> GetPagedAsync(
        EhrEndpointFilter filter, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.EhrEndpoints.AsQueryable();

        if (filter.Vendor.HasValue)
        {
            query = query.Where(x => x.Vendor == filter.Vendor.Value);
        }

        if (filter.IsActive.HasValue)
        {
            // Status is a free-text column, not an enum, and the listing renders anything that isn't exactly
            // "active" as Inactive — so "Inactive" here has to be a NOT-match, not an equality against some
            // second known value, or a row seeded with e.g. "retired" would vanish from both halves of the filter.
            query = filter.IsActive.Value
                ? query.Where(x => x.Status.ToLower() == ActiveStatus)
                : query.Where(x => x.Status.ToLower() != ActiveStatus);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Upper-cased on both sides rather than left to the database's own collation: SQL Server's default
            // collation is case-insensitive but PostgreSQL's LIKE is not, so an interpolated LIKE matched nothing
            // on Npgsql unless the user typed the stored casing exactly. Contains() (instead of an interpolated
            // LIKE pattern) also parameterizes the term, so a '%' or '_' the user types is matched literally
            // rather than acting as a wildcard.
            var term = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.Name.ToUpper().Contains(term)
                || x.FhirBaseUrl.ToUpper().Contains(term));
        }

        query = sortDescending switch
        {
            true => query.OrderByDescending(x => x.ModifiedOnUtc ?? x.CreatedOnUtc),
            false => query.OrderBy(x => x.ModifiedOnUtc ?? x.CreatedOnUtc),
            null => query.OrderBy(x => x.Name),
        };

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<EhrEndpoint>(items, totalCount, page, pageSize);
    }

    public async Task<IReadOnlyList<EhrEndpoint>> GetPublicAsync(
        EhrEndpointType endpointType, string? search, CancellationToken cancellationToken)
    {
        var query = _db.EhrEndpoints.Where(x => x.EndpointType == endpointType);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // See GetPagedAsync for why this upper-cases both sides rather than relying on the collation.
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(x => x.Name.ToUpper().Contains(term));
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<EhrEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.EhrEndpoints.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddAsync(EhrEndpoint endpoint, CancellationToken cancellationToken)
    {
        await _db.EhrEndpoints.AddAsync(endpoint, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(EhrEndpoint endpoint, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(EhrEndpoint endpoint, CancellationToken cancellationToken)
    {
        _db.EhrEndpoints.Remove(endpoint);
        return _db.SaveChangesAsync(cancellationToken);
    }
}
