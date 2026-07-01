using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using Microsoft.Extensions.Caching.Distributed;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// <see cref="IFhirAuthorizationCodeTokenStore"/> over <see cref="IDistributedCache"/> (Redis when a connection string
/// is configured, in-process distributed-memory otherwise). Persisting interactive tokens in the shared cache is what
/// lets the OAuth callback land on one node while a later pipeline run — on another API instance or the Worker — reads
/// the token back, and lets an authorized source survive a process restart. Refreshable tokens are retained well past
/// the short access-token lifetime so the stored refresh token stays usable; non-refreshable tokens expire with the
/// access token.
/// </summary>
public sealed class DistributedFhirAuthorizationCodeTokenStore : IFhirAuthorizationCodeTokenStore
{
    // Refresh tokens outlive access tokens; keep the entry alive long enough for the refresh grant to remain useful.
    private static readonly TimeSpan RefreshRetention = TimeSpan.FromDays(60);
    private const string KeyPrefix = "oauth-token:";

    private readonly IDistributedCache _cache;

    public DistributedFhirAuthorizationCodeTokenStore(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<StoredOAuthToken?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var payload = await _cache.GetStringAsync(KeyPrefix + key, cancellationToken);
        return payload is null ? null : JsonSerializer.Deserialize<StoredOAuthToken>(payload);
    }

    public Task SaveAsync(string key, StoredOAuthToken token, CancellationToken cancellationToken)
    {
        // Keep refreshable tokens well beyond the access-token expiry; otherwise let them lapse with the access token.
        var ttl = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? token.ExpiresOnUtc - DateTimeOffset.UtcNow
            : RefreshRetention;

        if (ttl <= TimeSpan.Zero)
        {
            // Already expired and not refreshable — nothing worth storing.
            return Task.CompletedTask;
        }

        var payload = JsonSerializer.Serialize(token);
        return _cache.SetStringAsync(
            KeyPrefix + key,
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);
    }
}
