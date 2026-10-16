using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryCdsTrustedIssuerRepository : ICdsTrustedIssuerRepository
{
    private readonly ConcurrentDictionary<Guid, CdsTrustedIssuer> _store = new();

    public Task<IReadOnlyList<CdsTrustedIssuer>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CdsTrustedIssuer>>(Live().OrderBy(x => x.Name).ToArray());

    public Task<CdsTrustedIssuer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Live().FirstOrDefault(x => x.Id == id));

    public Task<CdsTrustedIssuer?> GetEnabledByIssuerAsync(string issuer, CancellationToken cancellationToken) =>
        Task.FromResult(Live().FirstOrDefault(x => x.IsEnabled && string.Equals(x.Issuer, issuer, StringComparison.Ordinal)));

    public Task<bool> IssuerExistsAsync(string issuer, Guid? excludeId, CancellationToken cancellationToken) =>
        Task.FromResult(Live().Any(x =>
            string.Equals(x.Issuer, issuer, StringComparison.OrdinalIgnoreCase) && x.Id != excludeId));

    public Task AddAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken)
    {
        _store[issuer.Id] = issuer;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken)
    {
        _store[issuer.Id] = issuer;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken)
    {
        issuer.ApplyDeleted("system", DateTime.UtcNow);
        return Task.CompletedTask;
    }

    private IEnumerable<CdsTrustedIssuer> Live() => _store.Values.Where(x => !x.IsDeleted);
}

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryCdsServiceInstanceRepository : ICdsServiceInstanceRepository
{
    private readonly ConcurrentDictionary<Guid, CdsServiceInstance> _store = new();

    public Task<IReadOnlyList<CdsServiceInstance>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CdsServiceInstance>>(Live().OrderBy(x => x.Title).ToArray());

    public Task<IReadOnlyList<CdsServiceInstance>> GetEnabledAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CdsServiceInstance>>(Live().Where(x => x.IsEnabled).OrderBy(x => x.ServiceId).ToArray());

    public Task<CdsServiceInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Live().FirstOrDefault(x => x.Id == id));

    public Task<CdsServiceInstance?> GetByServiceIdAsync(string serviceId, CancellationToken cancellationToken) =>
        Task.FromResult(Live().FirstOrDefault(x => x.ServiceId == serviceId));

    public Task<bool> ServiceIdExistsAsync(string serviceId, Guid? excludeId, CancellationToken cancellationToken) =>
        Task.FromResult(Live().Any(x => x.ServiceId == serviceId && x.Id != excludeId));

    public Task AddAsync(CdsServiceInstance instance, CancellationToken cancellationToken)
    {
        _store[instance.Id] = instance;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CdsServiceInstance instance, CancellationToken cancellationToken)
    {
        _store[instance.Id] = instance;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CdsServiceInstance instance, CancellationToken cancellationToken)
    {
        instance.ApplyDeleted("system", DateTime.UtcNow);
        return Task.CompletedTask;
    }

    private IEnumerable<CdsServiceInstance> Live() => _store.Values.Where(x => !x.IsDeleted);
}

/// <summary>Used when no database connection string is configured (dev only). Keeps the newest entries only.</summary>
public sealed class InMemoryCdsCallLogRepository : ICdsCallLogRepository
{
    private const int Capacity = 5000;

    private readonly ConcurrentQueue<CdsCallLog> _entries = new();

    public Task AddAsync(CdsCallLog entry, CancellationToken cancellationToken)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<CdsCallLog>> GetPagedAsync(
        CdsCallLogFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var serviceId = filter.ServiceId?.Trim().ToLowerInvariant();
        var matches = _entries
            .Where(x => string.IsNullOrEmpty(serviceId) || x.ServiceId == serviceId)
            .Where(x => filter.Kind is null || x.Kind == filter.Kind)
            .Where(x => filter.Outcome is null || x.Outcome == filter.Outcome)
            .Where(x => filter.FromUtc is null || x.OccurredOnUtc >= filter.FromUtc)
            .Where(x => filter.ToUtc is null || x.OccurredOnUtc <= filter.ToUtc)
            .OrderByDescending(x => x.OccurredOnUtc)
            .ToArray();

        var items = matches.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return Task.FromResult(new PagedResult<CdsCallLog>(items, matches.Length, page, pageSize));
    }
}
