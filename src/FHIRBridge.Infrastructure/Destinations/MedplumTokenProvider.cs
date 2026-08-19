using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// <see cref="IMedplumTokenProvider"/> backed by the Medplum <c>/oauth2/token</c> endpoint using the OAuth2
/// <c>client_credentials</c> grant. Tokens (1-hour lived, no refresh) are cached in-memory per
/// (tokenUrl, clientId), refreshed proactively ~60s before expiry, and guarded by a per-key lock so a burst of
/// concurrent writes triggers only one token request. This is the configured-pipeline analog of the Runtime plane's
/// <c>DistributedFhirAccessTokenCache</c>; a distributed cache can be swapped in later without touching callers.
/// </summary>
public sealed class MedplumTokenProvider : IMedplumTokenProvider
{
    private static readonly TimeSpan ExpiryLeeway = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBackendServicesJwtFactory _jwtFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public MedplumTokenProvider(IHttpClientFactory httpClientFactory, IBackendServicesJwtFactory jwtFactory)
        : this(httpClientFactory, jwtFactory, () => DateTimeOffset.UtcNow)
    {
    }

    // Clock seam for tests.
    internal MedplumTokenProvider(
        IHttpClientFactory httpClientFactory, IBackendServicesJwtFactory jwtFactory, Func<DateTimeOffset> clock)
    {
        _httpClientFactory = httpClientFactory;
        _jwtFactory = jwtFactory;
        _clock = clock;
    }

    public async Task<string> GetAccessTokenAsync(
        string tokenUrl,
        MedplumClientCredential credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenUrl))
        {
            throw new ArgumentException("Medplum token URL is required.", nameof(tokenUrl));
        }

        if (string.IsNullOrWhiteSpace(credential.ClientId))
        {
            throw new ArgumentException("Medplum client id is required.", nameof(credential));
        }

        // Same client → same token, regardless of credential shape; key on (tokenUrl, clientId).
        var key = $"{tokenUrl}\n{credential.ClientId}";
        if (TryGetFresh(key, out var cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have refreshed while we waited on the lock.
            if (TryGetFresh(key, out cached))
            {
                return cached;
            }

            var (token, expiresAtUtc) = await RequestTokenAsync(tokenUrl, credential, cancellationToken);
            _cache[key] = new CachedToken(token, expiresAtUtc);
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(string key, out string token)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc - ExpiryLeeway > _clock())
        {
            token = entry.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private async Task<(string Token, DateTimeOffset ExpiresAtUtc)> RequestTokenAsync(
        string tokenUrl,
        MedplumClientCredential credential,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(MedplumTokenProvider));

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(BuildTokenForm(tokenUrl, credential))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Body may carry an OAuth error code; it is not PHI, but keep it terse.
            throw new HttpRequestException(
                $"Medplum token request to '{tokenUrl}' failed with {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var token = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new HttpRequestException($"Medplum token response from '{tokenUrl}' had no access_token.");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs)
            ? secs
            : 3600;
        return (token!, _clock().AddSeconds(expiresIn));
    }

    private IEnumerable<KeyValuePair<string, string>> BuildTokenForm(string tokenUrl, MedplumClientCredential credential)
    {
        switch (credential)
        {
            case MedplumClientSecretCredential secret:
                return
                [
                    new("grant_type", "client_credentials"),
                    new("client_id", secret.ClientId),
                    new("client_secret", secret.ClientSecret),
                ];

            case MedplumPrivateKeyJwtCredential jwt:
                // Medplum's SMART Backend Services flow keeps grant_type=client_credentials and carries a signed JWT
                // in client_assertion — the assertion, not the grant_type, signals JWT-bearer (see plan §3). aud MUST
                // be the token endpoint. Reuses the same RS384 signer as the Epic connector.
                var assertion = _jwtFactory.CreateClientAssertion(new BackendServicesJwtRequest(
                    jwt.ClientId, tokenUrl, jwt.PrivateKeyPem, jwt.KeyId, AssertionLifetime));
                return
                [
                    new("grant_type", "client_credentials"),
                    new("client_id", jwt.ClientId),
                    new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
                    new("client_assertion", assertion),
                ];

            default:
                throw new NotSupportedException(
                    $"Unsupported Medplum credential type '{credential.GetType().Name}'.");
        }
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAtUtc);
}
