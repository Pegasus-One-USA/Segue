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
