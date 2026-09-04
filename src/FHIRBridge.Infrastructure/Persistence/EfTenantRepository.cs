using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfTenantRepository : ITenantRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfTenantRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.Tenants.OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<PagedResult<Tenant>> GetPagedAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.Tenants
            .Where(x => string.IsNullOrWhiteSpace(search)
                || EF.Functions.Like(x.Name, $"%{search}%")
                || EF.Functions.Like(x.Code, $"%{search}%"))
            .OrderBy(x => x.Name);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<Tenant>(items, totalCount, page, pageSize);
    }

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Tenants.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.Tenants.FirstOrDefaultAsync(x => x.Code.ToLower() == code.ToLower(), cancellationToken);

    public Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken) =>
        _db.Tenants.AnyAsync(
            x => x.Code.ToLower() == code.ToLower() && (excludingId == null || x.Id != excludingId),
            cancellationToken);

    public Task<bool> HasUsersAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _db.Users.AnyAsync(x => x.TenantId == tenantId, cancellationToken);

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        await _db.Tenants.AddAsync(tenant, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _db.Tenants.Remove(tenant);
        return _db.SaveChangesAsync(cancellationToken);
    }
}
