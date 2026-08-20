using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;
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
public class SmartAuthorizationCodeTokenProvider : IFhirAccessTokenProvider, IInteractiveAuthorizationFlow, IFhirPatientContextProvider, IFhirGrantedScopeProvider
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
    /// Returns the actual <c>scope</c> the authorization server granted at this session's sign-in/last refresh
    /// (<see cref="StoredOAuthToken.Scope"/>), or null when no session is stored yet or the token endpoint never
    /// echoed one back. Purely a cache read — an interactive flow cannot mint a fresh token on demand (no user is
    /// present), so unlike the Backend Services provider this never triggers a network call.
    /// </summary>
    public async Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        var (_, stored) = await GetStoredTokenAsync(source, cancellationToken);
        return stored?.Scope;
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
            LogKeyLookup(source, key, stored is not null);
            return (key, stored);
        }

        var defaultKey = BuildStoreKey(source, null);
        var defaultStored = await _tokenStore.GetAsync(defaultKey, cancellationToken);
        LogKeyLookup(source, defaultKey, defaultStored is not null);
        return (defaultKey, defaultStored);
    }

    // Never logs patientId/CallerId values themselves (CallerId identifies a real end user, patientId a FHIR
    // resource id) — only the resolved key's shape (which segments it's built from) and a stable, non-reversible
    // hash of the full key, so two log lines for the same underlying session correlate ("this click's read found
    // the same key that click's write saved to") without ever printing an identifier.
    private void LogKeyLookup(FhirSourceConfiguration source, string key, bool hit)
    {
        _logger.LogInformation(
            "{Provider} token cache lookup: sourceConnectionId={SourceConnectionId} applicationType={ApplicationType} " +
            "keyedByCallerId={KeyedByCallerId} hasPatientId={HasPatientId} keyHash={KeyHash} hit={Hit}",
            ProviderName, source.SourceConnectionId, source.ApplicationType, IsCallerIdKeyed(source),
            !string.IsNullOrWhiteSpace(source.TargetPatientId), HashKey(key), hit);
    }

    private static string HashKey(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..12];

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

        // eClinicalWorks (Healow) deviates from the standard SMART authorize request in three confirmed ways (all
        // against a live authorize attempt): no PKCE, no v2 granular resource scopes, and a mandatory practice_code
        // parameter. Every ApplicationType strategy (Patient/Standalone/EhrLaunch) delegates to THIS one vendor-
        // neutral provider regardless of vendor (see PatientApplicationStrategy etc.), so all three must be checked
        // here directly via source.SourceType rather than as virtual hooks a vendor subclass would override — a
        // vendor subclass (e.g. HealowAuthorizationCodeTokenProvider) is never actually instantiated for those
        // strategies.
        var isHealow = source.SourceType == RuntimeSourceType.Healow;

        var resolvedScope = ResolveScopes(source, isEhrLaunch);
        if (isHealow)
        {
            // eCW's authorize endpoint rejects a SMART v2 granular scope suffix (patient/Patient.rs) with
            // invalid_scope. Rewrite every resource scope's suffix to v1's coarse ".read" regardless of the
            // SourceConnection's own configured/detected/persisted scope version, so an existing eCW connection
            // saved with v2 scopes doesn't need to be re-saved by an admin to work. Non-resource scopes (openid,
            // fhirUser, offline_access, launch/patient, ...) have no dot suffix and pass through unchanged.
            resolvedScope = NormalizeHealowScope(resolvedScope);
        }

        _logger.LogInformation(
            "[Step 4/6] {Provider} BuildAuthorizationRequest: sourceConnectionId={SourceConnectionId} " +
            "applicationType={ApplicationType} hasCallerId={HasCallerId} inputScopes=[{InputScopes}] " +
            "isEhrLaunch={IsEhrLaunch} resolvedScope=\"{ResolvedScope}\" usedFallbackDefault={UsedFallbackDefault}",
            ProviderName, source.SourceConnectionId, source.ApplicationType, !string.IsNullOrWhiteSpace(source.CallerId),
            string.Join(' ', source.Scopes), isEhrLaunch, resolvedScope, source.Scopes.Count == 0);

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = source.ClientId!;
        query["redirect_uri"] = redirectUri;
        query["state"] = state;
        query["scope"] = resolvedScope;

        // PKCE (RFC 7636) — every vendor except eClinicalWorks, whose live authorize endpoint has been confirmed to
        // reject/ignore it entirely. The code_verifier is still generated and returned above either way:
        // ExchangeAuthorizationCodeAsync always sends it on the token POST, which a server that never received a
        // code_challenge simply has nothing to validate it against.
        if (!isHealow)
        {
            query["code_challenge"] = Pkce.CreateS256Challenge(codeVerifier);
            query["code_challenge_method"] = "S256";
        }

        // eClinicalWorks (Healow) requires practice_code alongside aud — confirmed against a live authorize
        // request. Derived from source.BaseUrl's last path segment, which by this point already reflects a
        // resolved EhrEndpoint's own FhirBaseUrl when one applies (see
        // InteractiveSourceAuthorizationService.StartStandaloneCoreAsync's `baseUrl = ehrEndpoint?.FhirBaseUrl ??
        // sourceConnection.BaseUrl`) — eCW deploys its FHIR API per-practice as /fhir/r4/{practiceCode}, so no
        // separate config field is needed.
        if (isHealow)
        {
            var practiceCode = ExtractHealowPracticeCode(source.BaseUrl);
            if (!string.IsNullOrWhiteSpace(practiceCode))
            {
                query["practice_code"] = practiceCode;
            }
        }

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

    // See the eCW-specific block in BuildAuthorizationRequest above. Rewrites every "prefix/Type.<version-suffix>"
    // resource scope to "prefix/Type.read"; scopes with no dot suffix (openid, fhirUser, launch/patient, ...) pass
    // through unchanged.
    private static string NormalizeHealowScope(string resolvedScope)
    {
        var scopes = resolvedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < scopes.Length; i++)
        {
            var scope = scopes[i];
            var slashIndex = scope.IndexOf('/');
            if (slashIndex < 0)
            {
                continue;
            }

            var afterSlash = scope[(slashIndex + 1)..];
            var dotIndex = afterSlash.LastIndexOf('.');
            if (dotIndex < 0)
            {
                continue;
            }

            scopes[i] = $"{scope[..(slashIndex + 1 + dotIndex)]}.read";
        }

        return string.Join(' ', scopes);
    }

    // See the practice_code block in BuildAuthorizationRequest above. Null when the base URL isn't
    // configured/parseable yet (e.g. a source still being set up), so no blank practice_code is ever sent.
    private static string? ExtractHealowPracticeCode(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : null;
    }

    /// <summary>
    /// Completes the authorization-code grant from the OAuth callback and persists the resulting token. Returns the
    /// freshly minted access token.
    /// </summary>
    public async Task<SmartAuthorizationCodeExchangeResult> ExchangeAuthorizationCodeAsync(
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
        var result = await RequestAndStoreAsync(source, form, $"{ProviderName}TokenRefresh", cancellationToken, refreshToken);
        return result.AccessToken;
    }

    private async Task<SmartAuthorizationCodeExchangeResult> RequestAndStoreAsync(
        FhirSourceConfiguration source,
        Dictionary<string, string> form,
        string action,
        CancellationToken cancellationToken,
        string? fallbackRefreshToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyClientAuthentication(source, form, request);
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

            string? practitionerId = null;
            string? patientFromIdToken = null;
            if (!string.IsNullOrWhiteSpace(token.IdToken))
            {
                var (fhirUser, idTokenPatient) = await ValidateIdTokenAsync(source, token.IdToken!, cancellationToken);
                practitionerId = ExtractPractitionerId(fhirUser);
                patientFromIdToken = idTokenPatient ?? ExtractPatientId(fhirUser);
            }

            // Some SMART-on-FHIR EHRs don't surface `patient` at the top level of the token response for a Patient-
            // context app — only inside the id_token's own claims (either a bare `patient` claim, or the `fhirUser`
            // reference itself pointing at a Patient rather than a Practitioner). Only ever used when the top-level
            // field is absent, so this has no effect on a vendor (Epic, athenahealth) that already returns it there.
            var resolvedPatient = !string.IsNullOrWhiteSpace(token.Patient) ? token.Patient : patientFromIdToken;

            // resolvedPatient is a FHIR resource id, not logged in full elsewhere in this line — only its presence
            // is logged (never the value) to avoid writing patient identifiers into the log stream.
            _logger.LogInformation(
                "[Step 6/6] {Provider} {Action} succeeded: grantedScope=\"{GrantedScope}\" hasPatientContext={HasPatientContext} expiresInSeconds={ExpiresInSeconds}",
                ProviderName, action, token.Scope, !string.IsNullOrWhiteSpace(resolvedPatient), token.ExpiresInSeconds);

            var expiresIn = token.ExpiresInSeconds > 0 ? token.ExpiresInSeconds : DefaultExpiresInSeconds;
            var stored = new StoredOAuthToken(
                token.AccessToken!,
                string.IsNullOrWhiteSpace(token.RefreshToken) ? fallbackRefreshToken : token.RefreshToken,
                DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                token.Scope,
                resolvedPatient,
                source.TokenEndpoint,
                // By the time this runs, source.BaseUrl already carries whichever URL the caller actually issued
                // this session against (the connection's own, or a resolved hospital/organization EhrEndpoint
                // override) — capturing it here is what lets a later, separately triggered workflow run reuse it.
                source.BaseUrl);

            // Always save to the unscoped "default" slot — the pre-existing single-session behavior every caller
            // that doesn't specify TargetPatientId still relies on (e.g. the inline run triggered straight off this
            // same exchange, before any caller could know which patient just logged in).
            await SaveUnderBothKeysAsync(source, patientId: null, stored, cancellationToken);

            // ALSO save under a patient-specific key when a patient is known — either just returned by the token
            // endpoint (a fresh authorization_code exchange) or already known by the caller (a refresh, where
            // source.TargetPatientId was set to look the stored token up in the first place). This is what lets a
            // second, separately-triggered workflow ask for THIS patient's session explicitly and get it even after
            // a different patient has since logged in against the same source connection and overwritten "default".
            var resolvedPatientId = resolvedPatient ?? source.TargetPatientId;
            if (!string.IsNullOrWhiteSpace(resolvedPatientId))
            {
                await SaveUnderBothKeysAsync(source, resolvedPatientId, stored, cancellationToken);
            }

            await _auditSink.RecordAsync(source, $"{action}Succeeded", "Completed", $"{ProviderName} access token acquired.", cancellationToken);
            return new SmartAuthorizationCodeExchangeResult(
                token.AccessToken!,
                token.Scope,
                !string.IsNullOrWhiteSpace(resolvedPatient),
                HashKey(BuildStoreKey(source, null)),
                PatientId: resolvedPatient,
                PractitionerId: practitionerId);
        }
    }

    // Note: SmartAuthorizationRequest was moved to FHIRBridge.Runtime.Application.DTOs so the
    // IInteractiveAuthorizationFlow abstraction (Application layer) can reference it.

    // Adds client authentication to the token request for confidential clients. Public clients authenticate with PKCE
    // alone (no secret). Asymmetric (private_key_jwt) takes precedence over a symmetric client secret.
    private void ApplyClientAuthentication(FhirSourceConfiguration source, Dictionary<string, string> form, HttpRequestMessage request)
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
            // A confidential interactive client (secret present alongside PKCE — e.g. an athenahealth Patient/
            // Standalone app registered as confidential) can place that secret either in the form body ("post", the
            // default most SMART/FHIR token endpoints accept) or the Authorization header ("basic"). Some
            // authorization servers reject client_secret_post with invalid_client and require Basic instead —
            // mirrors OAuth2ClientCredentialsTokenProvider's identical AuthPlacement toggle for the Backend Services
            // grant, which this interactive flow previously ignored (always sent client_secret_post regardless of
            // the configured placement).
            if (string.Equals(source.AuthPlacement, "basic", StringComparison.OrdinalIgnoreCase))
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.ClientId}:{source.ClientSecret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }
            else
            {
                form["client_secret"] = source.ClientSecret!;
            }
        }
    }

    /// <summary>
    /// Validates the id_token's signature/issuer/audience/lifetime and returns its <c>fhirUser</c> claim (a
    /// SMART-standard reference such as <c>Practitioner/123</c> or an absolute URL ending in one, or null when the
    /// claim is absent) alongside its own <c>patient</c> claim, when present — the fallback source for a Patient-
    /// context app whose token response doesn't surface <c>patient</c> at the top level (see the caller).
    /// </summary>
    private async Task<(string? FhirUser, string? Patient)> ValidateIdTokenAsync(
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

        var principal = handler.ValidateToken(idToken, new TokenValidationParameters
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

        return (principal.FindFirst("fhirUser")?.Value, principal.FindFirst("patient")?.Value);
    }

    // Parses a SMART fhirUser reference (relative "Practitioner/123" or absolute ".../Practitioner/123") into the
    // bare Practitioner id, or null when it references a different resource type (e.g. Patient/RelatedPerson) or
    // doesn't parse as a reference at all.
    private static string? ExtractPractitionerId(string? fhirUserReference)
    {
        if (string.IsNullOrWhiteSpace(fhirUserReference))
        {
            return null;
        }

        var segments = fhirUserReference.TrimEnd('/').Split('/');
        return segments.Length >= 2 && string.Equals(segments[^2], "Practitioner", StringComparison.Ordinal)
            ? segments[^1]
            : null;
    }

    // Same shape as ExtractPractitionerId, but for a patient-facing app whose fhirUser reference points at the
    // Patient itself (e.g. a Patient Standalone launch with no separate top-level or id_token `patient` claim) —
    // the last-resort fallback ValidateIdTokenAsync's caller tries after the id_token's own `patient` claim.
    private static string? ExtractPatientId(string? fhirUserReference)
    {
        if (string.IsNullOrWhiteSpace(fhirUserReference))
        {
            return null;
        }

        var segments = fhirUserReference.TrimEnd('/').Split('/');
        return segments.Length >= 2 && string.Equals(segments[^2], "Patient", StringComparison.Ordinal)
            ? segments[^1]
            : null;
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

    // Token store key: for a Patient Standalone or Provider Standalone source with a caller id (both are
    // "directly-opened" flows with no EHR-supplied identity of their own — see
    // InteractiveSourceAuthorizationService.StartInteractiveFromContextAsync), key on the logged-in end user ALONE
    // (no SourceConnectionId segment) — every pipeline that shares that same user's session reuses the one token
    // their authorization already covers, instead of each SourceConnection needing its own separate consent screen.
    // This is a deliberate tradeoff, not an oversight: if two SourceConnections sharing a CallerId request
    // different scopes, whichever authorizes last silently overwrites the other's cached token in this slot — the
    // team has accepted that collision risk in favor of zero repeat-consent prompts. EHR Launch (which always
    // carries its own EHR-anchored patient/encounter context from the first request, with no client-side session
    // bootstrapping problem to solve) and a Standalone/Patient source with no caller id supplied fall back to the
    // pre-existing behavior: prefer the durable source-connection id, else the token endpoint + client identity.
    // The patient segment isolates concurrent sessions on the same key — "default" is the unscoped slot every
    // pre-existing caller (that never set TargetPatientId) reads/writes, so this is purely additive.
    private string BuildStoreKey(FhirSourceConfiguration source, string? patientId)
    {
        if (IsCallerIdKeyed(source))
        {
            return $"{ProviderName.ToLowerInvariant()}|{source.CallerId}|{patientId ?? "default"}";
        }

        return source.SourceConnectionId is { } id && id != Guid.Empty
            ? $"{ProviderName.ToLowerInvariant()}|{id}|{patientId ?? "default"}"
            : $"{ProviderName.ToLowerInvariant()}|{source.TokenEndpoint}|{source.ClientId}|{patientId ?? "default"}";
    }

    // Whether BuildStoreKey uses the CallerId-keyed slot for this source — Patient Standalone and Provider
    // Standalone only (both "directly-opened" flows with no EHR-supplied identity of their own), and only when a
    // caller id was actually supplied. Extracted so LogKeyLookup/LogKeySave's diagnostics can never drift out of
    // sync with BuildStoreKey's own condition.
    private static bool IsCallerIdKeyed(FhirSourceConfiguration source) =>
        source.ApplicationType is ApplicationType.Patient or ApplicationType.Standalone
            && !string.IsNullOrWhiteSpace(source.CallerId);

    // Saves under the CallerId-keyed slot ONLY for a Patient/Standalone source (see IsCallerIdKeyed) — this used to
    // ALSO dual-write a "legacy" per-SourceConnection slot (CallerId=null) so a caller that hadn't yet adopted
    // CallerId could still find its token. That legacy slot is a real cross-account data leak: it is shared by
    // EVERY end user of the same SourceConnection, and GetStoredTokenAsync/BuildStoreKey falls back to reading it
    // whenever a caller omits CallerId — which is exactly what happens for a brand-new HealthApp account that has
    // never connected before (its frontend has no sessionId to send yet). That account would then silently be
    // handed back whichever OTHER end user's token was last written to the shared slot, with no OAuth round trip
    // at all. The current frontend (Provider/Patient Standalone) always sends CallerId once it has one — the only
    // caller that can ever omit it is a truly first-time visitor, for whom "no token" is the only correct answer.
    // So the legacy write is now skipped entirely for CallerId-keyed sources; every other ApplicationType (which
    // never reaches IsCallerIdKeyed==true) is unaffected.
    private Task SaveUnderBothKeysAsync(
        FhirSourceConfiguration source,
        string? patientId,
        StoredOAuthToken stored,
        CancellationToken cancellationToken)
    {
        var key = BuildStoreKey(source, patientId);
        LogKeySave(source, key, patientId);
        return _tokenStore.SaveAsync(key, stored, cancellationToken);
    }

    // Same PHI-safe logging discipline as LogKeyLookup — see that method's remarks.
    private void LogKeySave(FhirSourceConfiguration source, string key, string? patientId)
    {
        _logger.LogInformation(
            "{Provider} token cache save: sourceConnectionId={SourceConnectionId} applicationType={ApplicationType} " +
            "keyedByCallerId={KeyedByCallerId} hasPatientId={HasPatientId} keyHash={KeyHash}",
            ProviderName, source.SourceConnectionId, source.ApplicationType, IsCallerIdKeyed(source),
            !string.IsNullOrWhiteSpace(patientId), HashKey(key));
    }

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
