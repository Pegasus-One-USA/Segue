using System.Collections.Concurrent;
using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Process-local <see cref="IFhirAccessTokenCache"/> fallback used when no distributed cache is injected (e.g. unit
/// tests that construct a token provider directly). Preserves the original per-process token-cache behaviour: a token
/// is reused until one minute before its expiry. Not shared across hosts and does not survive a restart — the
/// distributed implementation is used in the composed application.
/// </summary>
public sealed class InMemoryFhirAccessTokenCache : IFhirAccessTokenCache
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, CachedToken> Cache = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresOnUtc > DateTimeOffset.UtcNow.Add(ExpirySkew))
        {
            return Task.FromResult<string?>(cached.AccessToken);
        }

        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string cacheKey, string accessToken, DateTimeOffset expiresOnUtc, CancellationToken cancellationToken)
    {
        Cache[cacheKey] = new CachedToken(accessToken, expiresOnUtc);
        return Task.CompletedTask;
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresOnUtc);
}
