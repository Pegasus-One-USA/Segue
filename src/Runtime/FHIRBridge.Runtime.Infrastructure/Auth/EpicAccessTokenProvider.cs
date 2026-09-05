using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

public sealed class EpicAccessTokenProvider : IFhirAccessTokenProvider, IFhirGrantedScopeProvider
{
    private readonly HttpClient _httpClient;
    private readonly IBackendServicesJwtFactory _jwtFactory;
    private readonly IFhirAccessTokenCache _tokenCache;

    public EpicAccessTokenProvider(
        HttpClient httpClient,
        IBackendServicesJwtFactory jwtFactory,
        IFhirAccessTokenCache? tokenCache = null)
    {
        _httpClient = httpClient;
        _jwtFactory = jwtFactory;
        _tokenCache = tokenCache ?? new InMemoryFhirAccessTokenCache();
    }

    public async Task<string> GetAccessTokenAsync(
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        ValidateSource(source);

        var cacheKey = BuildCacheKey(source);

        var cachedToken = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cachedToken is not null)
        {
            return cachedToken;
        }

        var scopes = ResolveScopeString(source);
        var clientAssertion = _jwtFactory.CreateClientAssertion(new BackendServicesJwtRequest(
            source.ClientId!,
            source.TokenEndpoint!,
            source.PrivateKeyPem!,
            source.KeyId,
            TimeSpan.FromMinutes(5)));

        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = source.ClientId!,
                ["scope"] = scopes,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = clientAssertion
            })
        };
        TokenResponse tokenResponse;
        var response = await _httpClient.SendAsync(request, cancellationToken);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = await BuildFailureMessageAsync(
                    "Epic token endpoint",
                    response,
                    cancellationToken);

                throw new InvalidOperationException(message);
            }

            tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("Epic token endpoint returned an empty access token.");
            }

            var expiresIn = tokenResponse.ExpiresIn <= 0 ? 300 : tokenResponse.ExpiresIn;
            // Expire the cached token a minute early so a caller (e.g. the bulk-export per-file download loop) that
            // fetches it just before the real expiry still gets one with time left, rather than a token that lapses
            // mid-request and 401s. Floored so a very short-lived token is still cached briefly instead of never.
            var cacheLifetimeSeconds = Math.Max(30, expiresIn - 60);
            var expiresOnUtc = DateTimeOffset.UtcNow.AddSeconds(cacheLifetimeSeconds);
            await _tokenCache.SetAsync(cacheKey, tokenResponse.AccessToken, expiresOnUtc, cancellationToken);
            await _tokenCache.SetScopeAsync(cacheKey, tokenResponse.Scope, expiresOnUtc, cancellationToken);

            return tokenResponse.AccessToken;
        }
    }

    /// <summary>
    /// Returns Epic's actual granted <c>scope</c> response for this connection's client-credentials session, minting
    /// a token first if none is cached yet (cheap and non-interactive — unlike the interactive flows, there is no
    /// user to wait on). Null if Epic's token endpoint didn't echo a <c>scope</c> at all (some backend-services
    /// registrations don't), in which case a caller should fall back to <see cref="FhirSourceConfiguration.Scopes"/>.
    /// </summary>
    public async Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        await GetAccessTokenAsync(source, cancellationToken);
        return await _tokenCache.GetScopeAsync(BuildCacheKey(source), cancellationToken);
    }

    private static string BuildCacheKey(FhirSourceConfiguration source)
    {
        return $"fhir-token:epic|{source.TokenEndpoint}|{source.ClientId}|{ResolveScopeString(source)}";
    }

    /// <summary>
    /// The space-joined <c>scope</c> for this source, falling back to the vendor's system wildcard when no scopes
    /// were resolved at all (SourceConnectionRuntimeResolver normally regenerates them, so this is the
    /// nothing-configured path). The wildcard is vendor-specific: SMART has no literal <c>*.*</c> access level, and
    /// eClinicalWorks publishes <c>system/*.r</c> ONLY — it advertises no <c>system/*.read</c> at all, so the Epic
    /// spelling would be rejected outright with <c>invalid_grant</c>. Every other vendor keeps <c>system/*.read</c>,
    /// unchanged.
    /// </summary>
    private static string ResolveScopeString(FhirSourceConfiguration source)
    {
        if (source.Scopes.Count > 0)
        {
            return string.Join(' ', source.Scopes);
        }

        return source.SourceType == RuntimeSourceType.Healow ? "system/*.r" : "system/*.read";
    }

    private static void ValidateSource(FhirSourceConfiguration source)
    {
        if (string.IsNullOrWhiteSpace(source.TokenEndpoint) ||
            string.IsNullOrWhiteSpace(source.ClientId) ||
            string.IsNullOrWhiteSpace(source.PrivateKeyPem))
        {
            throw new InvalidOperationException("Epic SMART Backend Services token endpoint, client id, and private key are required.");
        }
    }

    private static async Task<string> BuildFailureMessageAsync(
        string endpointName,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"{endpointName} returned {(int)response.StatusCode} ({response.ReasonPhrase}).";

        if (string.IsNullOrWhiteSpace(body))
        {
            return message;
        }

        body = body.ReplaceLineEndings(" ").Trim();
        if (body.Length > 1000)
        {
            body = body[..1000] + "...";
        }

        return $"{message} Response body: {body}";
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string? Scope = null);
}
