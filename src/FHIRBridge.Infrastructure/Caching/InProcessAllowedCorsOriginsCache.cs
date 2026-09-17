using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Caching;

/// <summary>
/// Single-instance in-process cache. <see cref="Invalidate"/> (called by the admin screen's
/// add/update/delete) only clears THIS instance's copy — on a deployment that scales the API to
/// multiple replicas (e.g. Azure Container Apps' minReplicas/maxReplicas), an edit made through one
/// replica does not reach the others, which would otherwise keep serving their stale origin list
/// indefinitely (no expiry) until they happen to restart. The <see cref="MaxAge"/> bound below caps
/// that window to a few minutes on every replica regardless of which one handled the edit, instead of
/// requiring a redeploy to actually pick up an admin change. A true cross-replica push (Redis pub/sub,
/// matching DistributedFhirAccessTokenCache) would close that window immediately instead of bounding
/// it, if that's ever worth the added complexity.
/// </summary>
public sealed class InProcessAllowedCorsOriginsCache : IAllowedCorsOriginsCache
{
    // Portal's "Allowed Origins" admin screen tells the operator changes take effect within this
    // window — keep the two in sync if this changes.
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlySet<string> _configuredFloor;
    private volatile CacheEntry? _cached;

    public InProcessAllowedCorsOriginsCache(IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _configuredFloor = (configuration.GetSection("Portal:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:4200", "https://localhost:4200"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> GetOriginsAsync(CancellationToken cancellationToken)
    {
        var snapshot = _cached;
        if (snapshot is not null && DateTime.UtcNow - snapshot.CachedAtUtc < MaxAge)
        {
            return snapshot.Origins;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAllowedCorsOriginRepository>();
        var dbOrigins = await repository.GetAllAsync(cancellationToken);

        var merged = new HashSet<string>(_configuredFloor, StringComparer.OrdinalIgnoreCase);
        foreach (var origin in dbOrigins)
        {
            merged.Add(origin.OriginUrl);
        }

        _cached = new CacheEntry(merged, DateTime.UtcNow);
        return merged;
    }

    public void Invalidate() => _cached = null;

    private sealed record CacheEntry(IReadOnlySet<string> Origins, DateTime CachedAtUtc);
}
