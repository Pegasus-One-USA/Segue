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

    public async Task<IReadOnlyList<EhrEndpoint>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.EhrEndpoints.OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<PagedResult<EhrEndpoint>> GetPagedAsync(
        string? search, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.EhrEndpoints
            .Where(x => string.IsNullOrWhiteSpace(search)
                || EF.Functions.Like(x.Name, $"%{search}%")
                || EF.Functions.Like(x.FhirBaseUrl, $"%{search}%"));

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
        EhrEndpointType endpointType, string? search, CancellationToken cancellationToken) =>
        await _db.EhrEndpoints
            .Where(x => x.EndpointType == endpointType)
            .Where(x => string.IsNullOrWhiteSpace(search) || EF.Functions.Like(x.Name, $"%{search}%"))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

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
