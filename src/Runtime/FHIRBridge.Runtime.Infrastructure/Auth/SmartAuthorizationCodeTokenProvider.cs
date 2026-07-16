using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Web;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

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
public class SmartAuthorizationCodeTokenProvider : IFhirAccessTokenProvider, IInteractiveAuthorizationFlow, IFhirPatientContextProvider
{
    private const int DefaultExpiresInSeconds = 300;

    private readonly HttpClient _httpClient;
    private readonly IFhirAuthorizationCodeTokenStore _tokenStore;
    private readonly IFhirAccessTokenAuditSink _auditSink;
    private readonly IBackendServicesJwtFactory? _jwtFactory;
    private readonly ILogger _logger;

    public SmartAuthorizationCodeTokenProvider(
        HttpClient httpClient,
        IFhirAuthorizationCodeTokenStore tokenStore,
        IFhirAccessTokenAuditSink? auditSink = null,
        IBackendServicesJwtFactory? jwtFactory = null,
        ILogger<SmartAuthorizationCodeTokenProvider>? logger = null)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _auditSink = auditSink ?? new NoOpFhirAccessTokenAuditSink();
        _jwtFactory = jwtFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Human-readable provider name used in messages, audit actions, and the token-store key prefix.</summary>
    protected virtual string ProviderName => "SMART";

    public async Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.ClientId))
        {
            throw new InvalidOperationException($"{ProviderName} requires a client id.");
        }

        var (key, stored) = await GetStoredTokenAsync(source, cancellationToken);
        if (stored is null)
        {
            throw new InvalidOperationException(
                $"{ProviderName} source '{source.Name}' has no authorized token. Complete the interactive OAuth sign-in callback before running this pipeline.");
        }

        // A still-valid cached token is returned as-is: interactive sources discover their token endpoint at sign-in
        // and do not persist it on the source connection, so requiring one here would needlessly break the common
        // case (a pipeline run right after an EHR launch, when the token is fresh). The endpoint is only needed to
        // refresh an expired token below.
        if (stored.ExpiresOnUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return stored.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(stored.RefreshToken))
        {
            throw new InvalidOperationException(
                $"{ProviderName} token for source '{source.Name}' has expired and no refresh token is available. Re-authorize the source.");
        }

        // Prefer a token endpoint configured on the source; fall back to the one discovered and stashed at sign-in.
        var tokenEndpoint = string.IsNullOrWhiteSpace(source.TokenEndpoint) ? stored.TokenEndpoint : source.TokenEndpoint;
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            throw new InvalidOperationException(
                $"{ProviderName} token for source '{source.Name}' has expired and no token endpoint is available to refresh it. Re-authorize the source.");
        }

        return await RefreshAsync(source with { TokenEndpoint = tokenEndpoint }, key, stored.RefreshToken!, cancellationToken);
    }

    /// <summary>
    /// Returns the patient id established by the launch (the <c>patient</c> field the token endpoint returns for a
    /// <c>launch/patient</c> or patient-standalone flow), or null when the stored token carries no patient context.
    /// </summary>
    public async Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        var (_, stored) = await GetStoredTokenAsync(source, cancellationToken);
        return stored?.Patient;
    }

    /// <summary>
    /// Returns the FHIR base URL this session's launch actually resolved to (the source connection's own configured
    /// base URL, or a hospital/organization EhrEndpoint override), or null when nothing was stored for this
    /// (source connection, patient) session yet. Lets a later, separately triggered workflow run search against the
    /// same hospital's endpoint this session's login established, without needing to be told which hospital again.
    /// </summary>
    public async Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        var (_, stored) = await GetStoredTokenAsync(source, cancellationToken);
        return stored?.ResolvedBaseUrl;
    }

    /// <summary>
    /// Looks up this source's cached token, preferring the patient-specific slot (see <see cref="BuildStoreKey"/>)
    /// but falling back to the unscoped "default" slot when a <see cref="FhirSourceConfiguration.TargetPatientId"/>
    /// is set and nothing is cached under it yet. This is the common case for a non-patient-context connection
    /// (e.g. Provider/Backend Standalone): the sign-in itself never establishes a specific patient (no <c>patient</c>
    /// claim comes back), so only "default" is ever populated — every subsequent per-patient search (via
    /// WorkflowRunRequest.PatientId, purely to scope the FHIR query itself) would otherwise look like "no token"
    /// and wrongly force a fresh interactive sign-in. Returns the key the token was actually found under, so a
    /// caller that goes on to refresh an expired token updates the same slot it read from.
    /// </summary>
    private async Task<(string Key, StoredOAuthToken? Token)> GetStoredTokenAsync(
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var key = BuildStoreKey(source, source.TargetPatientId);
        var stored = await _tokenStore.GetAsync(key, cancellationToken);
        if (stored is not null || string.IsNullOrWhiteSpace(source.TargetPatientId))
        {
            return (key, stored);
        }

        var defaultKey = BuildStoreKey(source, null);
        var defaultStored = await _tokenStore.GetAsync(defaultKey, cancellationToken);
        return (defaultKey, defaultStored);
    }

    /// <summary>
    /// Clears both the request-time <see cref="FhirSourceConfiguration.TargetPatientId"/> slot (if set) and the
    /// unscoped "default" slot every save also writes to (see ExchangeAuthorizationCodeAsync/RefreshAsync) — so a
    /// caller that doesn't know (or isn't scoped to) a specific patient still fully discards whichever token this
    /// source's most recent interactive sign-in produced. Does not call the authorization server; the token remains
    /// technically valid at Epic/etc. until it naturally expires, it is just no longer usable from FHIRBridge.
    /// </summary>
    public async Task DiscardTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        await _tokenStore.RemoveAsync(BuildStoreKey(source, null), cancellationToken);
        if (!string.IsNullOrWhiteSpace(source.TargetPatientId))
        {
            await _tokenStore.RemoveAsync(BuildStoreKey(source, source.TargetPatientId), cancellationToken);
        }
    }

    /// <summary>
    /// Builds the authorization-endpoint URL the user is redirected to, returning the PKCE code verifier that must be
    /// retained (alongside <paramref name="state"/>) to complete <see cref="ExchangeAuthorizationCodeAsync"/>.
    /// </summary>
    public SmartAuthorizationRequest BuildAuthorizationRequest(
        FhirSourceConfiguration source,
        string redirectUri,
        string state,
        string? launch = null)
    {
        if (string.IsNullOrWhiteSpace(source.AuthorizationEndpoint) || string.IsNullOrWhiteSpace(source.ClientId))
        {
            throw new InvalidOperationException($"{ProviderName} requires an authorization endpoint and client id to start sign-in.");
        }

        var codeVerifier = Pkce.CreateCodeVerifier();
        var isEhrLaunch = launch is not null;
        var resolvedScope = ResolveScopes(source, isEhrLaunch);
        _logger.LogInformation(
            "[Step 4/6] {Provider} BuildAuthorizationRequest: sourceConnectionId={SourceConnectionId} " +
            "inputScopes=[{InputScopes}] isEhrLaunch={IsEhrLaunch} resolvedScope=\"{ResolvedScope}\" " +
            "usedFallbackDefault={UsedFallbackDefault}",
            ProviderName, source.SourceConnectionId, string.Join(' ', source.Scopes), isEhrLaunch, resolvedScope,
            source.Scopes.Count == 0);

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = source.ClientId!;
        query["redirect_uri"] = redirectUri;
        query["scope"] = resolvedScope;
        query["state"] = state;
        query["code_challenge"] = Pkce.CreateS256Challenge(codeVerifier);
        query["code_challenge_method"] = "S256";

        // Epic (and SMART generally) require the authorize request's audience to equal the FHIR base URL — omitting it
        // is the most common cause of a rejected launch.
        if (!string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            query["aud"] = source.BaseUrl;
        }

        // EHR launch: forward the opaque launch token so the EHR restores the patient/encounter context.
        if (!string.IsNullOrWhiteSpace(launch))
        {
            query["launch"] = launch;
        }

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
        ApplyClientAuthentication(source, form);

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

            if (!string.IsNullOrWhiteSpace(token.IdToken))
            {
                await ValidateIdTokenAsync(source, token.IdToken!, cancellationToken);
            }

            // token.Patient is a FHIR resource id, not logged in full elsewhere in this line — only its presence is
            // logged (never the value) to avoid writing patient identifiers into the log stream.
            _logger.LogInformation(
                "[Step 6/6] {Provider} {Action} succeeded: grantedScope=\"{GrantedScope}\" hasPatientContext={HasPatientContext} expiresInSeconds={ExpiresInSeconds}",
                ProviderName, action, token.Scope, !string.IsNullOrWhiteSpace(token.Patient), token.ExpiresInSeconds);

            var expiresIn = token.ExpiresInSeconds > 0 ? token.ExpiresInSeconds : DefaultExpiresInSeconds;
            var stored = new StoredOAuthToken(
                token.AccessToken!,
                string.IsNullOrWhiteSpace(token.RefreshToken) ? fallbackRefreshToken : token.RefreshToken,
                DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                token.Scope,
                token.Patient,
                source.TokenEndpoint,
                // By the time this runs, source.BaseUrl already carries whichever URL the caller actually issued
                // this session against (the connection's own, or a resolved hospital/organization EhrEndpoint
                // override) — capturing it here is what lets a later, separately triggered workflow run reuse it.
                source.BaseUrl);

            // Always save to the unscoped "default" slot — the pre-existing single-session behavior every caller
            // that doesn't specify TargetPatientId still relies on (e.g. the inline run triggered straight off this
            // same exchange, before any caller could know which patient just logged in).
            await _tokenStore.SaveAsync(BuildStoreKey(source, null), stored, cancellationToken);

            // ALSO save under a patient-specific key when a patient is known — either just returned by the token
            // endpoint (a fresh authorization_code exchange) or already known by the caller (a refresh, where
            // source.TargetPatientId was set to look the stored token up in the first place). This is what lets a
            // second, separately-triggered workflow ask for THIS patient's session explicitly and get it even after
            // a different patient has since logged in against the same source connection and overwritten "default".
            var resolvedPatientId = token.Patient ?? source.TargetPatientId;
            if (!string.IsNullOrWhiteSpace(resolvedPatientId))
            {
                await _tokenStore.SaveAsync(BuildStoreKey(source, resolvedPatientId), stored, cancellationToken);
            }

            await _auditSink.RecordAsync(source, $"{action}Succeeded", "Completed", $"{ProviderName} access token acquired.", cancellationToken);
            return token.AccessToken!;
        }
    }

    // Note: SmartAuthorizationRequest was moved to FHIRBridge.Runtime.Application.DTOs so the
    // IInteractiveAuthorizationFlow abstraction (Application layer) can reference it.

    // Adds client authentication to the token request for confidential clients. Public clients authenticate with PKCE
    // alone (no secret). Asymmetric (private_key_jwt) takes precedence over a symmetric client secret.
    private void ApplyClientAuthentication(FhirSourceConfiguration source, Dictionary<string, string> form)
    {
        if (!string.IsNullOrWhiteSpace(source.PrivateKeyPem))
        {
            if (_jwtFactory is null)
            {
                throw new InvalidOperationException(
                    $"{ProviderName} private_key_jwt client authentication requires a JWT factory to be configured.");
            }

            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = _jwtFactory.CreateClientAssertion(new BackendServicesJwtRequest(
                source.ClientId!,
                source.TokenEndpoint!,
                source.PrivateKeyPem!,
                source.KeyId,
                TimeSpan.FromMinutes(5)));
        }
        else if (!string.IsNullOrWhiteSpace(source.ClientSecret))
        {
            form["client_secret"] = source.ClientSecret!;
        }
    }

    private async Task ValidateIdTokenAsync(
        FhirSourceConfiguration source,
        string idToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.ClientId))
        {
            throw new InvalidOperationException($"{ProviderName} id_token validation requires a client id.");
        }

        var handler = new JwtSecurityTokenHandler();
        var unvalidated = handler.ReadJwtToken(idToken);
        if (string.IsNullOrWhiteSpace(unvalidated.Issuer))
        {
            throw new InvalidOperationException($"{ProviderName} token endpoint returned an id_token with no issuer.");
        }

        var metadataAddress = $"{unvalidated.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
        var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_httpClient) { RequireHttps = !IsLocalHttp(metadataAddress) });
        var configuration = await configurationManager.GetConfigurationAsync(cancellationToken);

        handler.ValidateToken(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuration.Issuer,
            ValidateAudience = true,
            ValidAudience = source.ClientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2)
        }, out _);
    }

    private static bool IsLocalHttp(string metadataAddress) =>
        Uri.TryCreate(metadataAddress, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp &&
        (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase));

    private static string ResolveScopes(FhirSourceConfiguration source, bool isEhrLaunch = false)
    {
        var scopes = source.Scopes.Count == 0
            ? ["launch/patient", "patient/*.read", "offline_access"]
            : source.Scopes.ToList();

        // EHR launch requires the plain "launch" scope alongside the opaque launch token.
        if (isEhrLaunch && !scopes.Contains("launch"))
        {
            scopes.Insert(0, "launch");
        }

        return string.Join(' ', scopes);
    }

    // Token store key: prefer the durable source-connection id; fall back to the token endpoint + client identity.
    // The patient segment isolates concurrent sessions on the same source connection — "default" is the unscoped
    // slot every pre-existing caller (that never set TargetPatientId) reads/writes, so this is purely additive.
    private string BuildStoreKey(FhirSourceConfiguration source, string? patientId) =>
        source.SourceConnectionId is { } id && id != Guid.Empty
            ? $"{ProviderName.ToLowerInvariant()}|{id}|{patientId ?? "default"}"
            : $"{ProviderName.ToLowerInvariant()}|{source.TokenEndpoint}|{source.ClientId}|{patientId ?? "default"}";

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

        // SMART returns the launch/selected patient id in the token response when a patient context is established.
        [JsonPropertyName("patient")]
        public string? Patient { get; set; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }
    }
}
