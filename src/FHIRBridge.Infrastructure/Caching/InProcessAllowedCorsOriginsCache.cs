using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FHIRBridge.Infrastructure.Caching;

/// <summary>
/// Single-instance in-process cache. <see cref="Invalidate"/> (called by the admin screen's
/// add/update/delete) only ever clears THIS instance's copy directly — on a deployment that scales the
/// API to multiple replicas (e.g. Azure Container Apps' minReplicas/maxReplicas), an edit made through
/// one replica would otherwise never reach the others, which would keep serving their stale origin list
/// indefinitely (no expiry) until they happen to restart.
///
/// Two layers close that gap, same as the RunStatusHub SignalR backplane (Program.cs) that already
/// depends on the same Redis connection:
///  1. When <see cref="IConnectionMultiplexer"/> is available, <see cref="Invalidate"/> also publishes
///     to a Redis channel every replica subscribes to on construction — every OTHER replica clears its
///     cache within about a round-trip, not up to <see cref="MaxAge"/> later.
///  2. <see cref="MaxAge"/> is kept as a fallback regardless: pub/sub delivery isn't guaranteed (a
///     replica mid-restart when the message published never sees it), and Redis may not be configured
///     at all (dev without docker-compose). Either way, every replica is never more than a few minutes
///     stale even if the publish is missed entirely.
/// </summary>
public sealed class InProcessAllowedCorsOriginsCache : IAllowedCorsOriginsCache
{
    private static readonly RedisChannel InvalidationChannel = RedisChannel.Literal("fhirbridge:cors-origins-invalidated");

    // Portal's "Allowed Origins" admin screen tells the operator changes take effect within this
    // window — keep the two in sync if this changes. Only ever the actual wait when Redis pub/sub
    // above isn't available or its message didn't arrive.
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlySet<string> _configuredFloor;
    private readonly IConnectionMultiplexer? _redis;
    private volatile CacheEntry? _cached;
    private volatile bool _subscribedToInvalidation;

    public InProcessAllowedCorsOriginsCache(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IConnectionMultiplexer? redis = null)
    {
        _scopeFactory = scopeFactory;
        _configuredFloor = (configuration.GetSection("Portal:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:4200", "https://localhost:4200"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _redis = redis;

        if (_redis is not null)
        {
            // AbortOnConnectFail: false (see DependencyInjection.cs) lets ConnectionMultiplexer.Connect
            // succeed even while Redis is unreachable — this constructor runs on the request path
            // (DynamicPortalCorsPolicyProvider, OAuthController, etc. all resolve this singleton), so a
            // SUBSCRIBE issued here must never be able to fail this call. Retrying on ConnectionRestored
            // means a replica that started during an outage still picks up cross-replica invalidation
            // once Redis is back, rather than falling back to MaxAge for the rest of its life.
            _redis.ConnectionRestored += (_, _) => TrySubscribeToInvalidation();
            TrySubscribeToInvalidation();
        }
    }

    // Subscribed unconditionally, including on the replica that itself publishes below — re-clearing an
    // already-null cache is a harmless no-op, and it's simpler than trying to skip self-notifies.
    //
    // Fire-and-forget async, not the synchronous Subscribe: Redis being merely SLOW (reachable but
    // hung) would otherwise block whichever request thread happens to construct this singleton for up
    // to SyncTimeout (5s default) before a RedisTimeoutException even gets a chance to be caught.
    // Catching bare Exception (not just RedisConnectionException) is deliberate for the same reason
    // that timeout throws: RedisTimeoutException derives from TimeoutException, not RedisException, so
    // a narrower catch lets the exact same "must never surface" failure through a narrower window
    // (Redis up but slow, instead of down) — the stated invariant is "never," not "never unless it's a
    // different exception type."
    private void TrySubscribeToInvalidation()
    {
        if (_redis is null || _subscribedToInvalidation) return;
        _ = SubscribeAsyncCore();

        async Task SubscribeAsyncCore()
        {
            try
            {
                await _redis.GetSubscriber().SubscribeAsync(InvalidationChannel, (_, _) => _cached = null);
                _subscribedToInvalidation = true;
            }
            catch (Exception)
            {
                // Still down/hung — ConnectionRestored will call this again; MaxAge bounds staleness meanwhile.
            }
        }
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

    public void Invalidate()
    {
        _cached = null;
        // Fire-and-forget: this replica doesn't need to wait on delivery, and a Redis hiccup here must
        // never turn an admin's "add origin" click into a failed request — MaxAge above still bounds
        // staleness on every replica regardless of whether this publish actually lands.
        _redis?.GetSubscriber().Publish(InvalidationChannel, RedisValue.EmptyString, CommandFlags.FireAndForget);
    }

    private sealed record CacheEntry(IReadOnlySet<string> Origins, DateTime CachedAtUtc);
}
