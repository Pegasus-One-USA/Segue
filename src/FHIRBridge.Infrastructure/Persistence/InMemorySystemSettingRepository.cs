using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemorySystemSettingRepository : ISystemSettingRepository
{
    private readonly ConcurrentDictionary<string, SystemSetting> _store = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SystemSetting>>(_store.Values.OrderBy(x => x.Key).ToArray());

    public Task<SystemSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken)
    {
        _store.TryGetValue(key, out var setting);
        return Task.FromResult(setting);
    }

    public Task<SystemSetting> UpsertAsync(
        string key, string value, string? description, CancellationToken cancellationToken)
    {
        var setting = _store.AddOrUpdate(
            key,
            _ => new SystemSetting(key, value, description),
            (_, existing) =>
            {
                existing.UpdateValue(value, description);
                return existing;
            });

        return Task.FromResult(setting);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
