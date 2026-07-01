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
public sealed class OAuth2ClientCredentialsTokenProvider : IFhirAccessTokenProvider
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
        var cacheKey = $"fhir-token:oauth2|{source.TokenEndpoint}|{source.ClientId}|{scopes}";
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
        await _tokenCache.SetAsync(cacheKey, token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn), cancellationToken);
        return token.AccessToken;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresInSeconds { get; set; }
    }
}
