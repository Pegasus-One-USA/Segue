using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Acquires an access token for MEDITECH Greenfield. Unlike the form-encoded OAuth 2.0 client-credentials grant,
/// Greenfield's token endpoint expects a confidential client (client id + secret) and a <c>application/json</c>
/// request body, defaults to a wildcard system scope, and issues short-lived (15-minute) tokens. Tokens are cached
/// per (token endpoint, client, scopes) until shortly before expiry.
/// </summary>
public sealed class MeditechGreenfieldTokenProvider : IFhirAccessTokenProvider
{
    private const string DefaultScope = "system/*.*";
    private const int DefaultExpiresInSeconds = 900;

    private readonly HttpClient _httpClient;
    private readonly IFhirAccessTokenAuditSink _auditSink;
    private readonly IFhirAccessTokenCache _tokenCache;

    public MeditechGreenfieldTokenProvider(
        HttpClient httpClient,
        IFhirAccessTokenAuditSink? auditSink = null,
        IFhirAccessTokenCache? tokenCache = null)
    {
        _httpClient = httpClient;
        _auditSink = auditSink ?? new NoOpFhirAccessTokenAuditSink();
        _tokenCache = tokenCache ?? new InMemoryFhirAccessTokenCache();
    }

    public async Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.TokenEndpoint))
        {
            throw new InvalidOperationException("MEDITECH Greenfield requires a token endpoint.");
        }

        if (string.IsNullOrWhiteSpace(source.ClientId) || string.IsNullOrWhiteSpace(source.ClientSecret))
        {
            throw new InvalidOperationException("MEDITECH Greenfield is a confidential client and requires a client id and secret.");
        }

        var scopes = source.Scopes.Count == 0 ? DefaultScope : string.Join(' ', source.Scopes);
        var cacheKey = $"fhir-token:meditech|{source.TokenEndpoint}|{source.ClientId}|{scopes}";
        var cached = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new TokenRequest(
            "client_credentials",
            source.ClientId!,
            source.ClientSecret!,
            scopes));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            await _auditSink.RecordAsync(source, "MeditechTokenRequestFailed", "Failed", exception.Message, cancellationToken);
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = $"MEDITECH Greenfield token endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}".Trim();
                await _auditSink.RecordAsync(source, "MeditechTokenRequestFailed", "Failed", message, cancellationToken);
                throw new InvalidOperationException(message);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("MEDITECH Greenfield token endpoint returned an empty response.");
            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                await _auditSink.RecordAsync(source, "MeditechTokenRequestFailed", "Failed", "MEDITECH Greenfield token endpoint did not return an access_token.", cancellationToken);
                throw new InvalidOperationException("MEDITECH Greenfield token endpoint did not return an access_token.");
            }

            var expiresIn = token.ExpiresInSeconds > 0 ? token.ExpiresInSeconds : DefaultExpiresInSeconds;
            await _tokenCache.SetAsync(cacheKey, token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn), cancellationToken);
            await _auditSink.RecordAsync(source, "MeditechTokenRequestSucceeded", "Completed", "MEDITECH Greenfield access token acquired.", cancellationToken);
            return token.AccessToken;
        }
    }

    private sealed record TokenRequest(
        [property: JsonPropertyName("grant_type")] string GrantType,
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_secret")] string ClientSecret,
        [property: JsonPropertyName("scope")] string Scope);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresInSeconds { get; set; }
    }
}
