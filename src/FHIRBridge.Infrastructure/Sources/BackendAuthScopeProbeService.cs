using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
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
    // per its own detected scope version).
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
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? DefaultScope : request.Scope;
        var authMethod = (request.AuthMethod ?? "jwt").Trim().ToLowerInvariant();

        var formFields = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope,
        };
        AuthenticationHeaderValue? basicAuth = null;

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
                formFields["client_id"] = request.ClientId;
                formFields["client_secret"] = request.ClientSecret ?? string.Empty;
            }
        }
        else
        {
            string privateKeyPem;
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

            formFields["client_id"] = request.ClientId;
            formFields["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            formFields["client_assertion"] = clientAssertion;
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(BackendAuthScopeProbeService));
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formFields),
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
                return new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(body, message) ?? message);
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return new BackendAuthScopesResult(false, [], "The token endpoint returned an empty access token.");
            }

            var grantedScopes = string.IsNullOrWhiteSpace(tokenResponse.Scope)
                ? []
                : tokenResponse.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new BackendAuthScopesResult(true, grantedScopes, null);
        }
        catch (Exception ex)
        {
            return new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(
                ex.Message, "Could not reach the token endpoint."));
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string? Scope);
}
