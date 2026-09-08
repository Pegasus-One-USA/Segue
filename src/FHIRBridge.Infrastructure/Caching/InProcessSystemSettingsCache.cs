using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Caching;

/// <summary>
/// Process-wide read cache for <see cref="FHIRBridge.Domain.Entities.SystemSetting"/> rows, backed by
/// the shared <see cref="IDistributedCache"/> (Redis in a multi-instance deployment, an in-process
/// memory cache when no Redis connection string is configured — see the "Phase 2" registration in
/// DependencyInjection.cs). Storing the snapshot there instead of a local field means an
/// <see cref="Invalidate"/> on one replica is visible to every other replica on their very next read,
/// instead of only after that replica happens to restart. The short absolute expiration below is a
/// safety net for the rare case an explicit Invalidate() is missed (e.g. a row edited directly in the
/// database, bypassing SystemSettingsService) — not the primary invalidation path.
/// </summary>
public sealed class InProcessSystemSettingsCache : ISystemSettingsCache
{
    private const string CacheKey = "fhirbridge:system-settings:v1";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedCache _distributedCache;

    public InProcessSystemSettingsCache(IServiceScopeFactory scopeFactory, IDistributedCache distributedCache)
    {
        _scopeFactory = scopeFactory;
        _distributedCache = distributedCache;
    }

    public async Task<string> GetStringAsync(string key, string defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    public async Task<double> GetDoubleAsync(string key, double defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    public void Invalidate() => _distributedCache.Remove(CacheKey);

    private async Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var cachedBytes = await _distributedCache.GetAsync(CacheKey, cancellationToken);
        if (cachedBytes is not null)
        {
            var cached = JsonSerializer.Deserialize<Dictionary<string, string>>(cachedBytes)
                ?? new Dictionary<string, string>();
            return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>();

        IReadOnlyList<FHIRBridge.Domain.Entities.SystemSetting> settings;
        try
        {
            settings = await repository.GetAllAsync(cancellationToken);
        }
        catch (DbException)
        {
            // The SystemSettings table doesn't exist yet — e.g. this read happens during startup, before
            // dbContext.Database.Migrate() has run. Don't cache the miss: once migrated, the next call
            // succeeds and every caller falls back to its own compiled-in/appsettings default meanwhile.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var merged = settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        await _distributedCache.SetAsync(
            CacheKey,
            JsonSerializer.SerializeToUtf8Bytes(merged),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheDuration },
            cancellationToken);
        return merged;
    }
}
