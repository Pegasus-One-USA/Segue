using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Web;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Vendor-neutral interactive SMART-on-FHIR access-token provider using the <c>authorization_code</c> grant with
/// PKCE (RFC 7636). It backs the EHR-launch, provider-standalone, and patient-standalone application types for any
/// vendor: <see cref="BuildAuthorizationRequest"/> starts the interactive sign-in and
/// <see cref="ExchangeAuthorizationCodeAsync"/> completes it from the OAuth callback, persisting the result in the
/// <see cref="IFhirAuthorizationCodeTokenStore"/>. <see cref="GetAccessTokenAsync"/> then reads the stored token
/// back, silently refreshing it with the refresh token when it nears expiry. Vendor subclasses (Epic, Healow, …)
/// need only override <see cref="ProviderName"/>.
/// </summary>
public class SmartAuthorizationCodeTokenProvider : IFhirAccessTokenProvider
{
    private const int DefaultExpiresInSeconds = 300;

    private readonly HttpClient _httpClient;
    private readonly IFhirAuthorizationCodeTokenStore _tokenStore;
    private readonly IFhirAccessTokenAuditSink _auditSink;

    public SmartAuthorizationCodeTokenProvider(
        HttpClient httpClient,
        IFhirAuthorizationCodeTokenStore tokenStore,
        IFhirAccessTokenAuditSink? auditSink = null)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _auditSink = auditSink ?? new NoOpFhirAccessTokenAuditSink();
    }

    /// <summary>Human-readable provider name used in messages, audit actions, and the token-store key prefix.</summary>
    protected virtual string ProviderName => "SMART";

    public async Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.TokenEndpoint) || string.IsNullOrWhiteSpace(source.ClientId))
        {
            throw new InvalidOperationException($"{ProviderName} requires a token endpoint and client id.");
        }

        var key = BuildStoreKey(source);
        var stored = await _tokenStore.GetAsync(key, cancellationToken);
        if (stored is null)
        {
            throw new InvalidOperationException(
                $"{ProviderName} source '{source.Name}' has no authorized token. Complete the interactive OAuth sign-in callback before running this pipeline.");
        }

        if (stored.ExpiresOnUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return stored.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(stored.RefreshToken))
        {
            throw new InvalidOperationException(
                $"{ProviderName} token for source '{source.Name}' has expired and no refresh token is available. Re-authorize the source.");
        }

        return await RefreshAsync(source, key, stored.RefreshToken!, cancellationToken);
    }

    /// <summary>
    /// Builds the authorization-endpoint URL the user is redirected to, returning the PKCE code verifier that must be
    /// retained (alongside <paramref name="state"/>) to complete <see cref="ExchangeAuthorizationCodeAsync"/>.
    /// </summary>
    public SmartAuthorizationRequest BuildAuthorizationRequest(
        FhirSourceConfiguration source,
        string redirectUri,
        string state)
    {
        if (string.IsNullOrWhiteSpace(source.AuthorizationEndpoint) || string.IsNullOrWhiteSpace(source.ClientId))
        {
            throw new InvalidOperationException($"{ProviderName} requires an authorization endpoint and client id to start sign-in.");
        }

        var codeVerifier = Pkce.CreateCodeVerifier();
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = source.ClientId!;
        query["redirect_uri"] = redirectUri;
        query["scope"] = ResolveScopes(source);
        query["state"] = state;
        query["code_challenge"] = Pkce.CreateS256Challenge(codeVerifier);
        query["code_challenge_method"] = "S256";

        var separator = source.AuthorizationEndpoint!.Contains('?') ? "&" : "?";
        var authorizationUrl = $"{source.AuthorizationEndpoint}{separator}{query}";
        return new SmartAuthorizationRequest(authorizationUrl, codeVerifier, state);
    }

    /// <summary>
    /// Completes the authorization-code grant from the OAuth callback and persists the resulting token. Returns the
    /// freshly minted access token.
    /// </summary>
    public async Task<string> ExchangeAuthorizationCodeAsync(
        FhirSourceConfiguration source,
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.TokenEndpoint) || string.IsNullOrWhiteSpace(source.ClientId))
        {
            throw new InvalidOperationException($"{ProviderName} requires a token endpoint and client id.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = source.ClientId!,
            ["code_verifier"] = codeVerifier
        };

        return await RequestAndStoreAsync(source, form, $"{ProviderName}AuthorizationCodeExchange", cancellationToken);
    }

    private async Task<string> RefreshAsync(
        FhirSourceConfiguration source,
        string key,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = source.ClientId!
        };

        // A rotated refresh token replaces the old one; if the endpoint omits one, keep the existing token.
        return await RequestAndStoreAsync(source, form, $"{ProviderName}TokenRefresh", cancellationToken, refreshToken);
    }

    private async Task<string> RequestAndStoreAsync(
        FhirSourceConfiguration source,
        Dictionary<string, string> form,
        string action,
        CancellationToken cancellationToken,
        string? fallbackRefreshToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            await _auditSink.RecordAsync(source, $"{action}Failed", "Failed", exception.Message, cancellationToken);
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = $"{ProviderName} token endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}".Trim();
                await _auditSink.RecordAsync(source, $"{action}Failed", "Failed", message, cancellationToken);
                throw new InvalidOperationException(message);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException($"{ProviderName} token endpoint returned an empty response.");
            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                await _auditSink.RecordAsync(source, $"{action}Failed", "Failed", $"{ProviderName} token endpoint did not return an access_token.", cancellationToken);
                throw new InvalidOperationException($"{ProviderName} token endpoint did not return an access_token.");
            }

            var expiresIn = token.ExpiresInSeconds > 0 ? token.ExpiresInSeconds : DefaultExpiresInSeconds;
            var stored = new StoredOAuthToken(
                token.AccessToken!,
                string.IsNullOrWhiteSpace(token.RefreshToken) ? fallbackRefreshToken : token.RefreshToken,
                DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                token.Scope);

            await _tokenStore.SaveAsync(BuildStoreKey(source), stored, cancellationToken);
            await _auditSink.RecordAsync(source, $"{action}Succeeded", "Completed", $"{ProviderName} access token acquired.", cancellationToken);
            return token.AccessToken!;
        }
    }

    private static string ResolveScopes(FhirSourceConfiguration source) =>
        source.Scopes.Count == 0 ? "launch/patient patient/*.read offline_access" : string.Join(' ', source.Scopes);

    // Token store key: prefer the durable source-connection id; fall back to the token endpoint + client identity.
    private string BuildStoreKey(FhirSourceConfiguration source) =>
        source.SourceConnectionId is { } id && id != Guid.Empty
            ? $"{ProviderName.ToLowerInvariant()}|{id}"
            : $"{ProviderName.ToLowerInvariant()}|{source.TokenEndpoint}|{source.ClientId}";

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresInSeconds { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}

/// <summary>The redirect URL plus the PKCE verifier and state that must be retained to complete the sign-in.</summary>
public sealed record SmartAuthorizationRequest(string AuthorizationUrl, string CodeVerifier, string State);
