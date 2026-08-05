using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Infrastructure.Destinations.Auth;

/// <summary>
/// OAuth2 client-credentials token acquisition for FHIR-repository destinations (e.g. Aidbox). Structurally mirrors
/// <c>OAuth2ClientCredentialsTokenProvider</c> (Runtime.Infrastructure) — same HttpClient + cache shape, same
/// request/response pattern — but decoupled from that provider's source-only DTO/dispatcher. Reuses the existing,
/// already-registered <see cref="IFhirAccessTokenCache"/> (Redis in prod, distributed memory otherwise); no new
/// cache is introduced. Cache keys are prefixed "fhir-dest-token:" (distinct from the source side's "fhir-token:")
/// so a destination's token can never collide with an EHR source's token in the shared distributed cache.
/// </summary>
public sealed class FhirDestinationOAuth2TokenProvider : IFhirDestinationTokenProvider
{
    private readonly HttpClient _httpClient;
    private readonly IFhirAccessTokenCache _tokenCache;

    public FhirDestinationOAuth2TokenProvider(HttpClient httpClient, IFhirAccessTokenCache tokenCache)
    {
        _httpClient = httpClient;
        _tokenCache = tokenCache;
    }

    public async Task<string> GetAccessTokenAsync(FhirDestinationOAuth2Options options, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(options.Scope) ? null : options.Scope;
        var cacheKey = $"fhir-dest-token:oauth2|{options.TokenEndpoint}|{options.ClientId}|{scope}";

        var cached = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
        };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"FHIR destination token request returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("FHIR destination token endpoint returned an empty response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("FHIR destination token endpoint did not return an access_token.");
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
