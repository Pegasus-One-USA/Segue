using System.Security.Cryptography;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// <see cref="IFhirAccessTokenCache"/> over <see cref="IDistributedCache"/> (Redis in dev/prod when a connection
/// string is configured, in-process distributed-memory otherwise). The token is stored with an absolute expiry set
/// one minute before the real token expiry, so a present value is always still usable and the cache evicts the entry
/// rather than handing out a token that is about to lapse mid-request. Values are wrapped with <see cref="IDataProtector"/>
/// before being written so a raw Redis read never yields a usable access token.
/// </summary>
public sealed class DistributedFhirAccessTokenCache : IFhirAccessTokenCache
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;

    public DistributedFhirAccessTokenCache(IDistributedCache cache, IDataProtectionProvider dataProtectionProvider)
    {
        _cache = cache;
        _protector = dataProtectionProvider.CreateProtector(
            "FHIRBridge.Runtime.Infrastructure.Auth.DistributedFhirAccessTokenCache.v1");
    }

    public async Task<string?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var payload = await _cache.GetStringAsync(cacheKey, cancellationToken);
        return Unprotect(payload);
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
            _protector.Protect(accessToken),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);
    }

    public async Task<string?> GetScopeAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var payload = await _cache.GetStringAsync(ScopeKey(cacheKey), cancellationToken);
        return Unprotect(payload);
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
            _protector.Protect(scope),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);
    }

    private string? Unprotect(string? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(payload);
        }
        catch (CryptographicException)
        {
            // Backward-compatible rollout: pre-hardening cache entries were plaintext. Treat as a cache miss
            // rather than surfacing a raw plaintext token or throwing — the caller re-authenticates as needed.
            return null;
        }
    }

    private static string ScopeKey(string cacheKey) => $"{cacheKey}:scope";
}
