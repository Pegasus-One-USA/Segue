using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfApiClientRepository : IApiClientRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfApiClientRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<ApiClient>> GetPagedAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.ApiClients.Include(x => x.ReturnUrls).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().ToLowerInvariant()}%";
            query = query.Where(x =>
                EF.Functions.Like(x.Name.ToLower(), pattern) || EF.Functions.Like(x.ClientId.ToLower(), pattern));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ApiClient>(items, totalCount, page, pageSize);
    }

    public async Task<ApiClient?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.ApiClients.Include(x => x.ReturnUrls).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<ApiClient?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken) =>
        await _db.ApiClients.Include(x => x.ReturnUrls).FirstOrDefaultAsync(x => x.ClientId == clientId, cancellationToken);

    public async Task<bool> ReturnUrlExistsAsync(Guid apiClientId, string url, CancellationToken cancellationToken) =>
        await _db.ApiClients
            .Where(x => x.Id == apiClientId)
            .SelectMany(x => x.ReturnUrls)
            .AnyAsync(x => x.Url.ToLower() == url.ToLower(), cancellationToken);

    public async Task AddAsync(ApiClient client, CancellationToken cancellationToken)
    {
        await _db.ApiClients.AddAsync(client, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(ApiClient client, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(ApiClient client, CancellationToken cancellationToken)
    {
        _db.ApiClients.Remove(client);
        return _db.SaveChangesAsync(cancellationToken);
    }
}
