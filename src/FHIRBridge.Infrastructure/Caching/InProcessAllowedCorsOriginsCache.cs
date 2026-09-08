using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Caching;

/// <summary>
/// The merged, live set of origins the API's "Portal" CORS policy currently allows: the permanent
/// Portal:AllowedOrigins config floor, unioned with rows in AllowedCorsOrigins. Backed by the shared
/// <see cref="IDistributedCache"/> (Redis in a multi-instance deployment, an in-process memory cache
/// otherwise — see the "Phase 2" registration in DependencyInjection.cs, the same one
/// DistributedFhirAccessTokenCache uses) instead of a local field, so an admin edit made through one
/// replica takes effect on every replica's very next request, not just the one that handled the edit.
/// </summary>
public sealed class InProcessAllowedCorsOriginsCache : IAllowedCorsOriginsCache
{
    private const string CacheKey = "fhirbridge:allowed-cors-origins:v1";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedCache _distributedCache;
    private readonly IReadOnlySet<string> _configuredFloor;

    public InProcessAllowedCorsOriginsCache(
        IServiceScopeFactory scopeFactory, IDistributedCache distributedCache, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _distributedCache = distributedCache;
        _configuredFloor = (configuration.GetSection("Portal:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:4200", "https://localhost:4200"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> GetOriginsAsync(CancellationToken cancellationToken)
    {
        var cachedBytes = await _distributedCache.GetAsync(CacheKey, cancellationToken);
        if (cachedBytes is not null)
        {
            var cachedOrigins = JsonSerializer.Deserialize<string[]>(cachedBytes) ?? [];
            return new HashSet<string>(cachedOrigins, StringComparer.OrdinalIgnoreCase);
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAllowedCorsOriginRepository>();
        var dbOrigins = await repository.GetAllAsync(cancellationToken);

        var merged = new HashSet<string>(_configuredFloor, StringComparer.OrdinalIgnoreCase);
        foreach (var origin in dbOrigins)
        {
            merged.Add(origin.OriginUrl);
        }

        await _distributedCache.SetAsync(
            CacheKey,
            JsonSerializer.SerializeToUtf8Bytes(merged),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheDuration },
            cancellationToken);
        return merged;
    }

    public void Invalidate() => _distributedCache.Remove(CacheKey);
}
