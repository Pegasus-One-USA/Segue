using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfCdsPayerRepository : ICdsPayerRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfCdsPayerRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CdsPayer>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.CdsPayers.OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public Task<CdsPayer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.CdsPayers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var lowered = name.Trim().ToLower();
        return await _db.CdsPayers.AnyAsync(x => x.Name.ToLower() == lowered && (excludeId == null || x.Id != excludeId), cancellationToken);
    }

    public async Task AddAsync(CdsPayer payer, CancellationToken cancellationToken)
    {
        await _db.CdsPayers.AddAsync(payer, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(CdsPayer payer, CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(CdsPayer payer, IReadOnlyList<CdsCoverageRule> rules, CancellationToken cancellationToken)
    {
        _db.CdsCoverageRules.RemoveRange(rules);
        _db.CdsPayers.Remove(payer);
        return _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfCdsCoverageRuleRepository : ICdsCoverageRuleRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfCdsCoverageRuleRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CdsCoverageRule>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.CdsCoverageRules.OrderBy(x => x.System).ThenBy(x => x.Code).ThenBy(x => x.EffectiveFrom).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CdsCoverageRule>> GetByPayerAsync(Guid payerId, CancellationToken cancellationToken) =>
        await _db.CdsCoverageRules.Where(x => x.PayerId == payerId).ToListAsync(cancellationToken);

    public async Task<PagedResult<CdsCoverageRule>> GetPagedAsync(
        CdsCoverageRuleFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.CdsCoverageRules.AsNoTracking();
        if (filter.PayerId is { } payerId)
        {
            query = query.Where(x => x.PayerId == payerId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var code = filter.Code.Trim().ToLower();
            query = query.Where(x => x.Code.ToLower().StartsWith(code));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLower();
            query = query.Where(x => x.Code.ToLower().Contains(search)
                                     || x.System.ToLower().Contains(search)
                                     || (x.Reason != null && x.Reason.ToLower().Contains(search)));
        }

        if (filter.IsEnabled is { } enabled)
        {
            query = query.Where(x => x.IsEnabled == enabled);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.System).ThenBy(x => x.Code).ThenBy(x => x.EffectiveFrom)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<CdsCoverageRule>(items, total, page, pageSize);
    }

    public Task<CdsCoverageRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.CdsCoverageRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<bool> KeyExistsAsync(
        Guid payerId, string system, string code, DateOnly effectiveFrom, Guid? excludeId, CancellationToken cancellationToken)
    {
        var loweredSystem = system.ToLower();
        var loweredCode = code.ToLower();
        return await _db.CdsCoverageRules.AnyAsync(x =>
            x.PayerId == payerId && x.System.ToLower() == loweredSystem && x.Code.ToLower() == loweredCode
            && x.EffectiveFrom == effectiveFrom && (excludeId == null || x.Id != excludeId), cancellationToken);
    }

    public async Task AddAsync(CdsCoverageRule rule, CancellationToken cancellationToken)
    {
        await _db.CdsCoverageRules.AddAsync(rule, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(CdsCoverageRule rule, CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(CdsCoverageRule rule, CancellationToken cancellationToken)
    {
        _db.CdsCoverageRules.Remove(rule);
        return _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ApplyAsync(
        IReadOnlyList<CdsCoverageRule> added,
        IReadOnlyList<CdsCoverageRule> updated,
        IReadOnlyList<CdsCoverageRule> deleted,
        CancellationToken cancellationToken)
    {
        // Deletes first: a replaced rule's unique key may be re-used by an added one. One SaveChanges = one transaction.
        _db.CdsCoverageRules.RemoveRange(deleted);
        await _db.CdsCoverageRules.AddRangeAsync(added, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryCdsPayerRepository : ICdsPayerRepository
{
    private readonly ConcurrentDictionary<Guid, CdsPayer> _store = new();

    public Task<IReadOnlyList<CdsPayer>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CdsPayer>>(Live().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<CdsPayer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Live().FirstOrDefault(x => x.Id == id));

    public Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken cancellationToken) =>
        Task.FromResult(Live().Any(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) && x.Id != excludeId));

    public Task AddAsync(CdsPayer payer, CancellationToken cancellationToken)
    {
        _store[payer.Id] = payer;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CdsPayer payer, CancellationToken cancellationToken)
    {
        _store[payer.Id] = payer;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CdsPayer payer, IReadOnlyList<CdsCoverageRule> rules, CancellationToken cancellationToken)
    {
        foreach (var rule in rules)
        {
            rule.ApplyDeleted("system", DateTime.UtcNow);
        }

        payer.ApplyDeleted("system", DateTime.UtcNow);
        return Task.CompletedTask;
    }

    private IEnumerable<CdsPayer> Live() => _store.Values.Where(x => !x.IsDeleted);
}

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryCdsCoverageRuleRepository : ICdsCoverageRuleRepository
{
    private readonly ConcurrentDictionary<Guid, CdsCoverageRule> _store = new();

    public Task<IReadOnlyList<CdsCoverageRule>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CdsCoverageRule>>(Ordered(Live()).ToArray());

    public Task<IReadOnlyList<CdsCoverageRule>> GetByPayerAsync(Guid payerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CdsCoverageRule>>(Live().Where(x => x.PayerId == payerId).ToArray());

    public Task<PagedResult<CdsCoverageRule>> GetPagedAsync(
        CdsCoverageRuleFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var code = filter.Code?.Trim();
        var search = filter.Search?.Trim();
        var matches = Ordered(Live()
                .Where(x => filter.PayerId is null || x.PayerId == filter.PayerId)
                .Where(x => string.IsNullOrEmpty(code) || x.Code.StartsWith(code, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrEmpty(search)
                            || x.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || x.System.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || (x.Reason?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .Where(x => filter.IsEnabled is null || x.IsEnabled == filter.IsEnabled))
            .ToArray();
        var items = matches.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return Task.FromResult(new PagedResult<CdsCoverageRule>(items, matches.Length, page, pageSize));
    }

    public Task<CdsCoverageRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Live().FirstOrDefault(x => x.Id == id));

    public Task<bool> KeyExistsAsync(
        Guid payerId, string system, string code, DateOnly effectiveFrom, Guid? excludeId, CancellationToken cancellationToken) =>
        Task.FromResult(Live().Any(x =>
            x.PayerId == payerId
            && string.Equals(x.System, system, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)
            && x.EffectiveFrom == effectiveFrom
            && x.Id != excludeId));

    public Task AddAsync(CdsCoverageRule rule, CancellationToken cancellationToken)
    {
        _store[rule.Id] = rule;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CdsCoverageRule rule, CancellationToken cancellationToken)
    {
        _store[rule.Id] = rule;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CdsCoverageRule rule, CancellationToken cancellationToken)
    {
        rule.ApplyDeleted("system", DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task ApplyAsync(
        IReadOnlyList<CdsCoverageRule> added,
        IReadOnlyList<CdsCoverageRule> updated,
        IReadOnlyList<CdsCoverageRule> deleted,
        CancellationToken cancellationToken)
    {
        foreach (var rule in deleted)
        {
            rule.ApplyDeleted("system", DateTime.UtcNow);
        }

        foreach (var rule in added.Concat(updated))
        {
            _store[rule.Id] = rule;
        }

        return Task.CompletedTask;
    }

    private IEnumerable<CdsCoverageRule> Live() => _store.Values.Where(x => !x.IsDeleted);

    private static IEnumerable<CdsCoverageRule> Ordered(IEnumerable<CdsCoverageRule> rules) =>
        rules.OrderBy(x => x.System, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.EffectiveFrom);
}
