using System.Collections.Concurrent;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// Signing keys of a trusted issuer: parsed from its pasted JWKS, or fetched from its JWKS URL and cached. A token
/// whose kid is not in the cached set triggers one refetch (key rotation), at most once per
/// <see cref="RefreshCooldown"/> per URL so a stream of bogus kids cannot turn into a stream of outbound fetches.
/// Only https URLs are fetched, and the named HttpClient refuses private, loopback and link-local addresses (see
/// <see cref="CdsJwksHttpHandler"/>).
/// </summary>
public sealed class CdsJwksProvider
{
    public const string HttpClientName = "CdsJwks";

    private const int MaxJwksBytes = 256 * 1024;
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(30);

    /// <summary>How long a failed fetch with no earlier good keys is remembered before the URL is tried again.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchGates = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<CdsOptions> _options;
    private readonly ILogger<CdsJwksProvider> _logger;

    public CdsJwksProvider(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<CdsOptions> options,
        ILogger<CdsJwksProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <summary>Keys from a pasted JWKS; empty when it cannot be parsed.</summary>
    public static IReadOnlyList<SecurityKey> ParseKeys(string? jwksJson)
    {
        if (string.IsNullOrWhiteSpace(jwksJson))
        {
            return [];
        }

        try
        {
            return new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// The cached keys of <paramref name="jwksUrl"/>, fetching them when absent/expired, or when
    /// <paramref name="refresh"/> is set and the cooldown has passed. Returns the last good set if a fetch fails.
    /// </summary>
    /// <remarks>
    /// Key lookup runs before the signature is verified, so anyone can make it happen. Three things keep that from
    /// turning into a stream of outbound fetches: a failed fetch is cached too (an empty set for
    /// <see cref="FailureBackoff"/>, or the last good set with a fresh attempt time), the cooldown counts every
    /// attempt rather than only successful ones, and concurrent misses for one URL share a single fetch.
    /// </remarks>
    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(string jwksUrl, bool refresh, CancellationToken cancellationToken)
    {
        // Also covers rows saved before the domain required https: keys fetched in clear text could be swapped.
        if (!CdsTrustedIssuer.IsHttpsUrl(jwksUrl))
        {
            _logger.LogWarning("CDS JWKS URL {JwksUrl} is not https; it is not fetched.", jwksUrl);
            return [];
        }

        var cacheKey = $"cds:jwks:{jwksUrl}";
        if (Usable(_cache.Get<CachedKeySet>(cacheKey), refresh) is { } hit)
        {
            return hit.Keys;
        }

        var gate = _fetchGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have fetched (or failed to) while this one waited for the gate.
            var cached = _cache.Get<CachedKeySet>(cacheKey);
            if (Usable(cached, refresh) is { } shared)
            {
                return shared.Keys;
            }

            var fetched = await FetchAsync(jwksUrl, cancellationToken);
            var keys = fetched ?? cached?.Keys ?? [];
            var lifetime = fetched is not null || keys.Count > 0
                ? TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.JwksCacheMinutes))
                : FailureBackoff;
            _cache.Set(cacheKey, new CachedKeySet(keys, DateTimeOffset.UtcNow), lifetime);
            return keys;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The cached entry when it may be used as is: present, and for a refresh, attempted within the cooldown.</summary>
    private static CachedKeySet? Usable(CachedKeySet? cached, bool refresh) =>
        cached is not null && (!refresh || DateTimeOffset.UtcNow - cached.AttemptedOnUtc < RefreshCooldown) ? cached : null;

    private async Task<IReadOnlyList<SecurityKey>?> FetchAsync(string jwksUrl, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.JwksFetchTimeoutSeconds)));

            using var response = await client.GetAsync(jwksUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CDS JWKS fetch from {JwksUrl} returned {StatusCode}.", jwksUrl, (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxJwksBytes)
            {
                _logger.LogWarning("CDS JWKS at {JwksUrl} is larger than {MaxBytes} bytes; ignored.", jwksUrl, MaxJwksBytes);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (json.Length > MaxJwksBytes)
            {
                _logger.LogWarning("CDS JWKS at {JwksUrl} is larger than {MaxBytes} bytes; ignored.", jwksUrl, MaxJwksBytes);
                return null;
            }

            var keys = ParseKeys(json);
            if (keys.Count == 0)
            {
                _logger.LogWarning("CDS JWKS at {JwksUrl} holds no usable signing keys.", jwksUrl);
                return null;
            }

            return keys;
        }
        catch (Exception exception) when ((exception is HttpRequestException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("CDS JWKS fetch from {JwksUrl} failed: {ErrorType}.", jwksUrl, exception.GetType().Name);
            return null;
        }
    }

    /// <param name="AttemptedOnUtc">When the last fetch was attempted, whether or not it succeeded.</param>
    private sealed record CachedKeySet(IReadOnlyList<SecurityKey> Keys, DateTimeOffset AttemptedOnUtc);
}
