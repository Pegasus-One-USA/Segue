using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Caching.Distributed;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// <see cref="IFhirAccessTokenCache"/> over <see cref="IDistributedCache"/> (Redis in dev/prod when a connection
/// string is configured, in-process distributed-memory otherwise). The token is stored with an absolute expiry set
/// one minute before the real token expiry, so a present value is always still usable and the cache evicts the entry
/// rather than handing out a token that is about to lapse mid-request.
/// </summary>
public sealed class DistributedFhirAccessTokenCache : IFhirAccessTokenCache
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private readonly IDistributedCache _cache;

    public DistributedFhirAccessTokenCache(IDistributedCache cache)
    {
        _cache = cache;
    }

    public Task<string?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        return _cache.GetStringAsync(cacheKey, cancellationToken);
    }

    public Task SetAsync(string cacheKey, string accessToken, DateTimeOffset expiresOnUtc, CancellationToken cancellationToken)
    {
        var ttl = expiresOnUtc - DateTimeOffset.UtcNow - ExpirySkew;
        if (ttl <= TimeSpan.Zero)
        {
            // Already at/near expiry — not worth caching for a sub-second window.
            return Task.CompletedTask;
        }

        return _cache.SetStringAsync(
            cacheKey,
            accessToken,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);
    }

    public Task<string?> GetScopeAsync(string cacheKey, CancellationToken cancellationToken)
    {
        return _cache.GetStringAsync(ScopeKey(cacheKey), cancellationToken);
    }

    public Task SetScopeAsync(string cacheKey, string? scope, DateTimeOffset expiresOnUtc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Task.CompletedTask;
        }

        var ttl = expiresOnUtc - DateTimeOffset.UtcNow - ExpirySkew;
        if (ttl <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        return _cache.SetStringAsync(
            ScopeKey(cacheKey),
            scope,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);
    }

    private static string ScopeKey(string cacheKey) => $"{cacheKey}:scope";
}
