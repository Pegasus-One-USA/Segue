using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Caching.Distributed;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// <see cref="IOAuthAuthorizationStateStore"/> over <see cref="IDistributedCache"/> (Redis when a connection string is
/// configured, in-process distributed-memory otherwise). Backing the in-flight sign-in state with the shared cache lets
/// the anonymous OAuth callback (and EHR-launch entry point) land on any node — not just the one that issued the
/// authorize redirect. Entries expire after a short window so an abandoned sign-in cannot be replayed, and are removed
/// on read so a <c>state</c> is single-use.
/// </summary>
public sealed class DistributedOAuthAuthorizationStateStore : IOAuthAuthorizationStateStore
{
    // A sign-in should complete promptly; an unused state is discarded so it cannot be replayed later.
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private const string KeyPrefix = "oauth-state:";

    private readonly IDistributedCache _cache;

    public DistributedOAuthAuthorizationStateStore(IDistributedCache cache)
    {
        _cache = cache;
    }

    public Task SaveAsync(string state, PendingAuthorization pending, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(pending);
        return _cache.SetStringAsync(
            KeyPrefix + state,
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StateLifetime },
            cancellationToken);
    }

    public async Task<PendingAuthorization?> TakeAsync(string state, CancellationToken cancellationToken)
    {
        var cacheKey = KeyPrefix + state;
        var payload = await _cache.GetStringAsync(cacheKey, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        // Single-use: remove before returning so the state cannot be replayed. (The authorization code is itself
        // single-use at the EHR token endpoint, so this get-then-remove is defense-in-depth rather than the sole guard.)
        await _cache.RemoveAsync(cacheKey, cancellationToken);
        return JsonSerializer.Deserialize<PendingAuthorization>(payload);
    }
}
