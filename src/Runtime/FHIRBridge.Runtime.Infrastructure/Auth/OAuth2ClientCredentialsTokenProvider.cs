using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Acquires an access token via the OAuth 2.0 client-credentials grant (RFC 6749 §4.4) — used by Cerner, Allscripts,
/// and generic FHIR R4 servers that authenticate with a client id + secret rather than SMART backend-services JWTs.
/// Tokens are cached per (token endpoint, client, scopes) until shortly before expiry.
/// </summary>
public sealed class OAuth2ClientCredentialsTokenProvider : IFhirAccessTokenProvider, IFhirGrantedScopeProvider
{
    private readonly HttpClient _httpClient;
    private readonly IFhirAccessTokenCache _tokenCache;

    public OAuth2ClientCredentialsTokenProvider(HttpClient httpClient, IFhirAccessTokenCache? tokenCache = null)
    {
        _httpClient = httpClient;
        _tokenCache = tokenCache ?? new InMemoryFhirAccessTokenCache();
    }

    public async Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.TokenEndpoint))
        {
            throw new InvalidOperationException("OAuth2 client-credentials requires a token endpoint.");
        }

        if (string.IsNullOrWhiteSpace(source.ClientId) || string.IsNullOrWhiteSpace(source.ClientSecret))
        {
            throw new InvalidOperationException("OAuth2 client-credentials requires a client id and secret.");
        }

        var scopes = source.Scopes.Count == 0 ? "system/*.read" : string.Join(' ', source.Scopes);
        var cacheKey = BuildCacheKey(source, scopes);
        var cached = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = source.ClientId!,
            ["client_secret"] = source.ClientSecret!,
            ["scope"] = scopes
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"OAuth2 token request returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("OAuth2 token endpoint returned an empty response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("OAuth2 token endpoint did not return an access_token.");
        }

        var expiresIn = token.ExpiresInSeconds > 0 ? token.ExpiresInSeconds : 300;
        var expiresOnUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        await _tokenCache.SetAsync(cacheKey, token.AccessToken, expiresOnUtc, cancellationToken);
        await _tokenCache.SetScopeAsync(cacheKey, token.Scope, expiresOnUtc, cancellationToken);
        return token.AccessToken;
    }

    /// <summary>
    /// Returns this connection's actual granted <c>scope</c> response, minting a token first if none is cached yet
    /// (client-credentials is non-interactive, so this never needs to wait on a user). Null if the token endpoint
    /// never echoed a <c>scope</c> back.
    /// </summary>
    public async Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        await GetAccessTokenAsync(source, cancellationToken);
        var scopes = source.Scopes.Count == 0 ? "system/*.read" : string.Join(' ', source.Scopes);
        return await _tokenCache.GetScopeAsync(BuildCacheKey(source, scopes), cancellationToken);
    }

    private static string BuildCacheKey(FhirSourceConfiguration source, string scopes) =>
        $"fhir-token:oauth2|{source.TokenEndpoint}|{source.ClientId}|{scopes}";

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresInSeconds { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}
