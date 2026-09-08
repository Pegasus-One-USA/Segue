using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;

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

        var formFields = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = source.ClientId!,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = clientAssertion
        };
        // OAuth2 makes 'scope' OPTIONAL, and omitting it is not the same as sending it empty: an empty value is a
        // request for no scopes (which servers are entitled to reject), whereas omission asks the server to apply
        // whatever the client registration already grants. ResolveScopeString returns empty only where no
        // wildcard is safe to guess at (athenahealth), so leave the field off rather than send 'scope='.
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            formFields["scope"] = scopes;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formFields)
        };
        TokenResponse tokenResponse;
        var response = await _httpClient.SendAsync(request, cancellationToken);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The status is carried as a NUMBER on the exception, not just as text inside its message, so the
                // diagnosis rule can tell an Epic outage (5xx) from a genuine credentials rejection (400/401)
                // instead of substring-searching for "invalid_client" and defaulting everything else to
                // "check your client ID" — see TokenEndpointException's remarks.
                throw TokenEndpointException.FromResponse(
                    "Epic",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    await response.Content.ReadAsStringAsync(cancellationToken));
            }

            tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw TokenEndpointException.EmptyResponse("Epic", "an empty access token.");
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
    /// spelling would be rejected outright with <c>invalid_grant</c>. athenahealth has no usable wildcard at all
    /// and gets empty, which the caller turns into an omitted <c>scope</c> field. Every other vendor keeps
    /// <c>system/*.read</c>, unchanged.
    /// </summary>
    private static string ResolveScopeString(FhirSourceConfiguration source)
    {
        if (source.Scopes.Count > 0)
        {
            return string.Join(' ', source.Scopes);
        }

        return source.SourceType switch
        {
            // eClinicalWorks publishes 'system/*.r' ONLY — see the remarks above.
            RuntimeSourceType.Healow => "system/*.r",
            // athenahealth has no usable wildcard AT ALL: its authorization server rejects the ENTIRE token
            // request (401 access_denied, verified live against the sandbox) the moment a wildcard resource scope
            // appears — so every spelling of '*' is worse than sending nothing. Returning empty lets athenahealth
            // apply whatever the app registration already grants, which at least has a chance of succeeding,
            // instead of guaranteeing a failure. Reaching here at all means no scopes were resolved, which for
            // athenahealth is itself the misconfiguration to fix (see EpicSourceConnectionScopeSyncService, which
            // keeps an enumerated resource-type list on the connection precisely so this path isn't taken).
            RuntimeSourceType.Athenahealth => string.Empty,
            _ => "system/*.read",
        };
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

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string? Scope = null);
}
