using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfAllowedCorsOriginRepository : IAllowedCorsOriginRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfAllowedCorsOriginRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AllowedCorsOrigin>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.AllowedCorsOrigins.OrderBy(x => x.OriginUrl).ToListAsync(cancellationToken);

    public async Task<PagedResult<AllowedCorsOrigin>> GetPagedAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.AllowedCorsOrigins.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Matches the screen's single search box against both columns it displays. EF.Functions.Like
            // keeps the filter in SQL (a client-side Contains would pull every row back first, defeating
            // the point of paging) and is case-insensitive on both supported providers.
            var pattern = $"%{search.Trim()}%";
            query = query.Where(x =>
                EF.Functions.Like(x.OriginUrl, pattern)
                || (x.Label != null && EF.Functions.Like(x.Label, pattern)));
        }

        // Count before Skip/Take so the paginator reports the size of the FILTERED set, not the page.
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(x => x.OriginUrl)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AllowedCorsOrigin>(items, totalCount, page, pageSize);
    }

    public async Task<AllowedCorsOrigin?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.AllowedCorsOrigins.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<bool> ExistsAsync(string originUrl, CancellationToken cancellationToken) =>
        await _db.AllowedCorsOrigins.AnyAsync(
            x => x.OriginUrl.ToLower() == originUrl.ToLower(), cancellationToken);

    public async Task AddAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken)
    {
        await _db.AllowedCorsOrigins.AddAsync(origin, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(AllowedCorsOrigin origin, CancellationToken cancellationToken)
    {
        _db.AllowedCorsOrigins.Remove(origin);
        return _db.SaveChangesAsync(cancellationToken);
    }
}
