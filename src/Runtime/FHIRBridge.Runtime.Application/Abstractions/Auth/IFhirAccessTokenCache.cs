namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

/// <summary>
/// Caches acquired FHIR source access tokens keyed by a provider-scoped cache key. Backed by the distributed cache
/// (Redis when configured, in-process memory otherwise) so a token minted by the API is reused by the Worker and
/// survives a host restart. Implementations apply a safety skew so a token is treated as expired shortly before its
/// real expiry.
/// </summary>
public interface IFhirAccessTokenCache
{
    /// <summary>Returns the cached access token for <paramref name="cacheKey"/>, or null if absent/expired.</summary>
    Task<string?> GetAsync(string cacheKey, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="accessToken"/> until shortly before <paramref name="expiresOnUtc"/>.</summary>
    Task SetAsync(string cacheKey, string accessToken, DateTimeOffset expiresOnUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the granted <c>scope</c> string cached alongside the access token for <paramref name="cacheKey"/>
    /// (see <see cref="SetScopeAsync"/>), or null if absent/expired/never returned by the token endpoint.
    /// </summary>
    Task<string?> GetScopeAsync(string cacheKey, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the authorization server's actual granted <c>scope</c> response alongside the access token, so a later
    /// caller can read back what was really granted (vs. what was requested) without re-minting a token. Same expiry
    /// as the access token it was minted with.
    /// </summary>
    Task SetScopeAsync(string cacheKey, string? scope, DateTimeOffset expiresOnUtc, CancellationToken cancellationToken);
}
