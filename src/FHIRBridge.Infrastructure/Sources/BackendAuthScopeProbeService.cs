using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Pre-create counterpart to <see cref="SourceEndpointProbeService"/>: instead of just reading SMART metadata, this
/// actually performs a client_credentials token exchange against the source's token endpoint, so the wizard's
/// Discover action (and its explicit "Test Connection" gate) can show the scopes the source really granted the app
/// — as opposed to the scopes it merely advertises support for in .well-known/smart-configuration — for either of
/// FHIRBridge's Backend System auth methods (see <see cref="BackendAuthScopesRequest.AuthMethod"/>). For "jwt", the
/// signing key is resolved from the secret store by reference — the raw PEM is never sent from or held by the
/// browser. For "secret", the client secret is not yet in the secret store at this point in the wizard flow (that
/// only happens when the connection is saved), so it's taken directly off the request for this one-off exchange.
/// </summary>
public sealed class BackendAuthScopeProbeService : IBackendAuthScopeProbeService
{
    // The system scope grammar most Backend Services servers (Epic included) implement has no literal wildcard
    // access-level — 'system/*.*' is rejected as invalid_scope. The access level must be a real suffix ('.read'
    // for v1, '.rs' for v2); this fallback only applies when the caller didn't specify one (the wizard always does,
    // per its own detected scope version). Vendors whose vocabulary differs from that generic grammar are respelled
    // from their VendorScopeCatalog profile instead — see ResolveRequestedScope.
    private const string DefaultScope = "system/*.read";

    private readonly ISecretProvider _secretProvider;
    private readonly IBackendServicesJwtFactory _jwtFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BackendAuthScopeProbeService> _logger;

    public BackendAuthScopeProbeService(
        ISecretProvider secretProvider,
        IBackendServicesJwtFactory jwtFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<BackendAuthScopeProbeService> logger)
    {
        _secretProvider = secretProvider;
        _jwtFactory = jwtFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<BackendAuthScopesResult> ProbeGrantedScopesAsync(
        BackendAuthScopesRequest request,
        CancellationToken cancellationToken)
    {
        var scopeAttempts = BuildScopeAttempts(request);
        var authMethod = (request.AuthMethod ?? "jwt").Trim().ToLowerInvariant();

        var credentialFields = new Dictionary<string, string>();
        AuthenticationHeaderValue? basicAuth = null;
        string? privateKeyPem = null;

        if (authMethod == "secret")
        {
            var placement = string.IsNullOrWhiteSpace(request.AuthPlacement)
                ? "post"
                : request.AuthPlacement.Trim().ToLowerInvariant();
            if (placement == "basic")
            {
                var raw = $"{request.ClientId}:{request.ClientSecret}";
                basicAuth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
            }
            else
            {
                credentialFields["client_id"] = request.ClientId;
                credentialFields["client_secret"] = request.ClientSecret ?? string.Empty;
            }
        }
        else
        {
            // Resolved once — the key itself doesn't change between attempts, only the assertion signed with it.
            try
            {
                privateKeyPem = await _secretProvider.GetSecretAsync(
                    new SecretReference(request.PrivateKeyVaultName!, request.PrivateKeySecretName!),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(
                    ex.Message, "Could not retrieve the configured signing key."));
            }
        }

        BackendAuthScopesResult? firstFailure = null;
        foreach (var scope in scopeAttempts)
        {
            var fields = new Dictionary<string, string>(credentialFields)
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = scope,
            };

            if (privateKeyPem is not null)
            {
                // A NEW assertion per attempt, never a reused one: a client assertion's jti is single-use, and a
                // server that enforces that (eCW rejects a replayed jti with a bare 400 invalid_request) would
                // answer the retry with a replay error instead of the truth about the scope being tested —
                // masking the real reason the first attempt failed.
                string clientAssertion;
                try
                {
                    clientAssertion = _jwtFactory.CreateClientAssertion(new BackendServicesJwtRequest(
                        request.ClientId,
                        request.TokenEndpoint,
                        privateKeyPem,
                        request.KeyId,
                        TimeSpan.FromMinutes(5)));
                }
                catch (Exception ex)
                {
                    return new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(
                        ex.Message, "Could not sign the client assertion."));
                }

                fields["client_id"] = request.ClientId;
                fields["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                fields["client_assertion"] = clientAssertion;
            }

            var (result, scopeRejected) = await ExchangeAsync(request, fields, basicAuth, scope, cancellationToken);

            if (result.Success)
            {
                return result;
            }

            // The first failure is the one reported: it describes the scope string this connection will really
            // request at run time, where a later attempt only says whether a deliberately narrowed scope fared
            // better. Every attempt is logged with its own scope in ExchangeAsync either way.
            firstFailure ??= result;

            // Only a scope rejection is worth another attempt — a bad client id, a bad assertion, or an
            // unreachable endpoint fails identically no matter what scope is asked for, and retrying would just
            // double the wait before showing the operator the real reason.
            if (!scopeRejected)
            {
                break;
            }
        }

        return firstFailure ?? new BackendAuthScopesResult(false, [], "No scope was available to request.");
    }

    /// <summary>
    /// One client_credentials exchange. Returns the probe result plus whether the failure was specifically the
    /// authorization server rejecting the requested <paramref name="scope"/> (OAuth <c>invalid_scope</c>), which is
    /// the only failure the caller retries under a narrower scope.
    /// </summary>
    private async Task<(BackendAuthScopesResult Result, bool ScopeRejected)> ExchangeAsync(
        BackendAuthScopesRequest request,
        IReadOnlyDictionary<string, string> fields,
        AuthenticationHeaderValue? basicAuth,
        string scope,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(BackendAuthScopeProbeService));
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        if (basicAuth is not null)
        {
            httpRequest.Headers.Authorization = basicAuth;
        }

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = string.Format(
                    "Backend-services token exchange failed for client {0} against {1} with scope '{2}': {3} {4} — {5}",
                    request.ClientId,
                    request.TokenEndpoint,
                    scope,
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    body);
                // The client-facing message is sanitized (an arbitrary external response could contain markup or an
                // oversized payload) — log the raw body here so a real invalid_scope/invalid_client reason is still
                // diagnosable in Seq without exposing it to the browser. No token/JWT/secret/key material is ever in
                // this body; it's the source server's own OAuth error response.
                _logger.LogWarning(
                    "Backend-services token exchange failed for client {ClientId} against {TokenEndpoint} " +
                    "with scope '{Scope}': {StatusCode} {ReasonPhrase} — {Body}",
                    request.ClientId, request.TokenEndpoint, scope, (int)response.StatusCode, response.ReasonPhrase, body);
                return (
                    new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(body, message) ?? message),
                    WorthNarrowingScope(response.StatusCode, body));
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return (
                    new BackendAuthScopesResult(false, [], "The token endpoint returned an empty access token."),
                    false);
            }

            var grantedScopes = string.IsNullOrWhiteSpace(tokenResponse.Scope)
                ? []
                : tokenResponse.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return (new BackendAuthScopesResult(true, grantedScopes, null), false);
        }
        catch (Exception ex)
        {
            return (
                new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(
                    ex.Message, "Could not reach the token endpoint.")),
                false);
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string? Scope);

    /// <summary>
    /// The scope strings to try, in order: what the caller asked for (respelled for the vendor — see
    /// <see cref="ResolveRequestedScope"/>), then a narrower <c>system/Patient</c> attempt.
    /// <para>
    /// The wildcard is the wizard's stand-in for "whatever this app is allowed", but a Backend Services app is
    /// registered against an explicit list of resource scopes on several vendors (eCW's dev portal being the case
    /// in hand), and asking for a wildcard those registrations don't include fails the whole exchange with
    /// <c>invalid_scope</c>. Since this probe exists to answer "do these credentials work?", falling back to the
    /// one resource type every backend registration grants gives that answer instead of a scope-shaped false
    /// negative on a gate that blocks saving the connection.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> BuildScopeAttempts(BackendAuthScopesRequest request)
    {
        var scope = ResolveRequestedScope(request);
        var narrowed = $"system/Patient.{PatientAccessLevel(request, scope)}";

        return string.Equals(scope, narrowed, StringComparison.OrdinalIgnoreCase)
            ? new[] { scope }
            : new[] { scope, narrowed };
    }

    /// <summary>
    /// How this vendor spells Patient's system read access level: from its <see cref="VendorScopeCatalog"/> profile
    /// when it has one, else carried over from the access level the caller's own scope string already uses (so an
    /// Epic v2 connection narrows to <c>.rs</c>, a v1 one to <c>.read</c>) — <c>read</c> as the last resort.
    /// </summary>
    private static string PatientAccessLevel(BackendAuthScopesRequest request, string scope)
    {
        var profile = VendorScopeCatalog.For(ParseVendor(request.Vendor));
        if (profile is not null && profile.TryGetReadAccessLevel("Patient", out var vendorAccessLevel))
        {
            return vendorAccessLevel;
        }

        var firstScope = scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var dot = firstScope?.LastIndexOf('.') ?? -1;
        return dot > 0 ? firstScope![(dot + 1)..] : "read";
    }

    /// <summary>
    /// Whether a rejected exchange is worth one more attempt under a narrower scope. A named <c>invalid_scope</c>
    /// obviously is; so is a bare 400, because a vendor that reports a scope its app registration doesn't include
    /// as a generic <c>invalid_request</c> (eCW does exactly that) is indistinguishable by error code alone from a
    /// genuinely malformed request. A named credential fault never is — no scope change fixes those — and neither
    /// is any other status, which keeps the whole probe to at most two round trips.
    /// </summary>
    private static bool WorthNarrowingScope(HttpStatusCode statusCode, string body)
    {
        if (body.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (statusCode != HttpStatusCode.BadRequest)
        {
            return false;
        }

        return !body.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
            && !body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scope string actually sent to the token endpoint. For a vendor with no
    /// <see cref="VendorScopeCatalog"/> profile this is the caller's scope verbatim (or
    /// <see cref="DefaultScope"/>) — today's behaviour. For a registered vendor, every <c>system/</c> scope is
    /// respelled with the access level that vendor really advertises, exactly as
    /// <see cref="ScopeGeneratorService"/> already does for a saved connection's scope string.
    /// <para>
    /// Without this, the wizard's credential gate tests eCW with the generic <c>system/*.rs</c> (or
    /// <c>system/*.read</c>) whose only accepted spelling there is <c>system/*.r</c>, and eCW rejects the whole
    /// token request with <c>invalid_scope</c> — reporting a credential failure for credentials that are fine.
    /// </para>
    /// </summary>
    private static string ResolveRequestedScope(BackendAuthScopesRequest request)
    {
        var requested = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope.Trim();
        var profile = VendorScopeCatalog.For(ParseVendor(request.Vendor));

        if (profile is null)
        {
            return requested ?? DefaultScope;
        }

        var wildcard = $"system/*.{profile.WildcardReadAccessLevel}";
        if (requested is null)
        {
            return wildcard;
        }

        var respelled = requested
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(candidate => RespellForVendor(candidate, profile))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Every requested scope naming a resource type this vendor publishes no system read scope for leaves
        // nothing to ask for — fall back to the vendor's own wildcard rather than sending an empty scope.
        return respelled.Count == 0 ? wildcard : string.Join(' ', respelled);
    }

    /// <summary>
    /// One scope, respelled for <paramref name="profile"/>: a non-<c>system/</c> scope (openid, fhirUser, ...) is
    /// kept as-is, a resource wildcard takes the vendor's wildcard access level, a supported resource type takes
    /// that resource's advertised access level, and an unsupported resource type is dropped (null) — asking for a
    /// scope the server doesn't publish is what fails the entire token request.
    /// </summary>
    private static string? RespellForVendor(string scope, VendorScopeProfile profile)
    {
        var slash = scope.IndexOf('/');
        if (slash < 0 || !scope.AsSpan(0, slash).Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return scope;
        }

        var rest = scope[(slash + 1)..];
        var dot = rest.LastIndexOf('.');
        var resourceType = dot < 0 ? rest : rest[..dot];

        if (resourceType == "*")
        {
            return $"system/*.{profile.WildcardReadAccessLevel}";
        }

        return profile.TryGetReadAccessLevel(resourceType, out var accessLevel)
            ? $"system/{resourceType}.{accessLevel}"
            : null;
    }

    private static SourceSystemType? ParseVendor(string? vendor) =>
        Enum.TryParse<SourceSystemType>(vendor, ignoreCase: true, out var parsed) ? parsed : null;
}
