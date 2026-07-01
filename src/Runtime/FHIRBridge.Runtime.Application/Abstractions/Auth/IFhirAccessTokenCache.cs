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
}
