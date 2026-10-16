using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// One-time use of a CDS client token's jti. Marking a key is an atomic set-if-absent, so two concurrent requests
/// carrying the same captured token cannot both be accepted: with Redis configured it is a single
/// <c>SET key 1 NX PX</c> shared by every node; without Redis it is a get-then-set serialized inside this process,
/// which protects a single node only. A multi-node deployment needs ConnectionStrings:Redis for replay protection.
/// </summary>
public sealed class CdsReplayGuard
{
    private const string RedisKeyPrefix = "fhirbridge:";

    private readonly IDistributedCache _cache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly SemaphoreSlim _localGate = new(1, 1);

    public CdsReplayGuard(IDistributedCache cache, IConnectionMultiplexer? redis = null)
    {
        _cache = cache;
        _redis = redis;
    }

    /// <summary>
    /// True when the record of used tokens is in Redis (shared by every node); false when it is this process's
    /// memory, which protects a single node only. Reported by the go-live readiness check.
    /// </summary>
    public bool IsShared => _redis is not null;

    /// <summary>Marks <paramref name="key"/> as used until <paramref name="expiresOnUtc"/>.</summary>
    /// <returns>True when this call marked it; false when it was already used (a replay).</returns>
    public async Task<bool> TryMarkUsedAsync(string key, DateTimeOffset expiresOnUtc, CancellationToken cancellationToken)
    {
        var lifetime = expiresOnUtc - DateTimeOffset.UtcNow;
        if (lifetime <= TimeSpan.Zero)
        {
            lifetime = TimeSpan.FromSeconds(1);
        }

        if (_redis is not null)
        {
            return await _redis.GetDatabase().StringSetAsync(RedisKeyPrefix + key, "1", lifetime, When.NotExists);
        }

        await _localGate.WaitAsync(cancellationToken);
        try
        {
            if (await _cache.GetAsync(key, cancellationToken) is not null)
            {
                return false;
            }

            await _cache.SetAsync(key, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime }, cancellationToken);
            return true;
        }
        finally
        {
            _localGate.Release();
        }
    }
}
