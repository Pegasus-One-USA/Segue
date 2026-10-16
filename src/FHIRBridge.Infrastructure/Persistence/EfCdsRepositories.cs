using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfCdsTrustedIssuerRepository : ICdsTrustedIssuerRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfCdsTrustedIssuerRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CdsTrustedIssuer>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.CdsTrustedIssuers.OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<CdsTrustedIssuer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.CdsTrustedIssuers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<CdsTrustedIssuer?> GetEnabledByIssuerAsync(string issuer, CancellationToken cancellationToken)
    {
        // The database collation may compare case-insensitively (SQL Server's default does); iss is an exact,
        // case-sensitive match, so the final comparison is made here.
        var candidates = await _db.CdsTrustedIssuers
            .AsNoTracking()
            .Where(x => x.IsEnabled && x.Issuer == issuer)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(x => string.Equals(x.Issuer, issuer, StringComparison.Ordinal));
    }

    public Task<bool> IssuerExistsAsync(string issuer, Guid? excludeId, CancellationToken cancellationToken) =>
        _db.CdsTrustedIssuers.AnyAsync(x => x.Issuer == issuer && (excludeId == null || x.Id != excludeId), cancellationToken);

    public async Task AddAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken)
    {
        await _db.CdsTrustedIssuers.AddAsync(issuer, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken)
    {
        _db.CdsTrustedIssuers.Remove(issuer);
        return _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfCdsServiceInstanceRepository : ICdsServiceInstanceRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfCdsServiceInstanceRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CdsServiceInstance>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.CdsServiceInstances.OrderBy(x => x.Title).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CdsServiceInstance>> GetEnabledAsync(CancellationToken cancellationToken) =>
        await _db.CdsServiceInstances.AsNoTracking().Where(x => x.IsEnabled).OrderBy(x => x.ServiceId).ToListAsync(cancellationToken);

    public async Task<CdsServiceInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.CdsServiceInstances.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<CdsServiceInstance?> GetByServiceIdAsync(string serviceId, CancellationToken cancellationToken) =>
        await _db.CdsServiceInstances.FirstOrDefaultAsync(x => x.ServiceId == serviceId, cancellationToken);

    public Task<bool> ServiceIdExistsAsync(string serviceId, Guid? excludeId, CancellationToken cancellationToken) =>
        _db.CdsServiceInstances.AnyAsync(x => x.ServiceId == serviceId && (excludeId == null || x.Id != excludeId), cancellationToken);

    public async Task AddAsync(CdsServiceInstance instance, CancellationToken cancellationToken)
    {
        await _db.CdsServiceInstances.AddAsync(instance, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(CdsServiceInstance instance, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(CdsServiceInstance instance, CancellationToken cancellationToken)
    {
        _db.CdsServiceInstances.Remove(instance);
        return _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfCdsCallLogRepository : ICdsCallLogRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfCdsCallLogRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(CdsCallLog entry, CancellationToken cancellationToken)
    {
        await _db.CdsCallLogs.AddAsync(entry, cancellationToken);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Same as EfGovernanceLogger.PersistAsync: a row left Added on the scoped context would be resent, and
            // fail again, by every later save in this request (e.g. the security event that follows a rejection).
            _db.Entry(entry).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<PagedResult<CdsCallLog>> GetPagedAsync(
        CdsCallLogFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.CdsCallLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.ServiceId))
        {
            var serviceId = filter.ServiceId.Trim().ToLowerInvariant();
            query = query.Where(x => x.ServiceId == serviceId);
        }

        if (filter.Kind.HasValue)
        {
            query = query.Where(x => x.Kind == filter.Kind.Value);
        }

        if (filter.Outcome.HasValue)
        {
            query = query.Where(x => x.Outcome == filter.Outcome.Value);
        }

        if (filter.FromUtc.HasValue)
        {
            query = query.Where(x => x.OccurredOnUtc >= filter.FromUtc.Value);
        }

        if (filter.ToUtc.HasValue)
        {
            query = query.Where(x => x.OccurredOnUtc <= filter.ToUtc.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<CdsCallLog>(items, totalCount, page, pageSize);
    }
}
