using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;

    public OAuth2ClientCredentialsTokenProvider(
        HttpClient httpClient,
        IFhirAccessTokenCache? tokenCache = null,
        ILogger<OAuth2ClientCredentialsTokenProvider>? logger = null)
    {
        _httpClient = httpClient;
        _tokenCache = tokenCache ?? new InMemoryFhirAccessTokenCache();
        _logger = logger ?? NullLogger<OAuth2ClientCredentialsTokenProvider>.Instance;
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

        // The "no scopes configured yet" default (a bare wildcard resource scope) is safe for most vendors here —
        // Cerner/Allscripts/generic FHIR R4 servers accept it. athenahealth's authorization server does not: it
        // rejects the ENTIRE token request with a flat 401 "access_denied" / "Policy evaluation failed" the moment
        // a wildcard resource scope appears (verified live against the preview sandbox), rather than granting a
        // narrower subset. An empty Scopes list here almost always means the connection has no resource types
        // selected yet (SourceConnectionRuntimeResolver regenerates scopes fresh from Retrieval.ResourceTypes on
        // every run — an empty resource-type list produces an empty scope list) — surfacing that as a clear,
        // actionable configuration error is far more useful than a cryptic vendor-side 401 with no indication of
        // what to fix.
        if (source.Scopes.Count == 0 && source.SourceType == RuntimeSourceType.Athenahealth)
        {
            throw new InvalidOperationException(
                "This athenahealth source connection has no resource types configured, so no OAuth scope can be " +
                "requested — athenahealth rejects a wildcard scope outright. Select at least one resource type " +
                "(directly, or by wiring this source to a destination's selected resources) before running.");
        }

        var scopes = source.Scopes.Count == 0 ? "system/*.read" : string.Join(' ', source.Scopes);
        var authPlacement = string.Equals(source.AuthPlacement, "basic", StringComparison.OrdinalIgnoreCase) ? "basic" : "post";
        var cacheKey = BuildCacheKey(source, scopes);
        var cached = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            _logger.LogInformation(
                "OAuth2 client-credentials: using cached token for {TokenEndpoint} clientId={ClientId} placement={AuthPlacement} scope=\"{Scope}\"",
                source.TokenEndpoint, source.ClientId, authPlacement, scopes);
            return cached;
        }

        _logger.LogInformation(
            "OAuth2 client-credentials: requesting token from {TokenEndpoint} clientId={ClientId} placement={AuthPlacement} scope=\"{Scope}\"",
            source.TokenEndpoint, source.ClientId, authPlacement, scopes);

        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Most servers accept client id/secret in the form body ("post" — the default). Some (athenahealth's docs
        // call this out explicitly, though preview accepted "post") only accept them via the Authorization header
        // instead ("basic") — a per-connection toggle rather than a retry-on-failure, since a server that rejects
        // one placement typically returns a plain 401 with no signal to distinguish "wrong placement" from "wrong
        // credentials."
        if (string.Equals(source.AuthPlacement, "basic", StringComparison.OrdinalIgnoreCase))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.ClientId}:{source.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = scopes
            });
        }
        else
        {
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = source.ClientId!,
                ["client_secret"] = source.ClientSecret!,
                ["scope"] = scopes
            });
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "OAuth2 client-credentials token request FAILED: {StatusCode} ({ReasonPhrase}) from {TokenEndpoint} " +
                "clientId={ClientId} placement={AuthPlacement} scope=\"{Scope}\" — response body: {Body}",
                (int)response.StatusCode, response.ReasonPhrase, source.TokenEndpoint, source.ClientId, authPlacement, scopes, body);
            throw new InvalidOperationException(
                $"OAuth2 token request returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("OAuth2 token endpoint returned an empty response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("OAuth2 token endpoint did not return an access_token.");
        }

        _logger.LogInformation(
            "OAuth2 client-credentials: token acquired from {TokenEndpoint} clientId={ClientId} grantedScope=\"{GrantedScope}\" expiresInSeconds={ExpiresIn}",
            source.TokenEndpoint, source.ClientId, token.Scope, token.ExpiresInSeconds);

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
