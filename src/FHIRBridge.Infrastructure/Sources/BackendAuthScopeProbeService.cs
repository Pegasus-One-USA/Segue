using System.Net.Http.Json;
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
/// actually performs the client_credentials + private_key_jwt exchange against Epic's token endpoint, so the
/// wizard's Discover action can show the scopes Epic really granted the app (as opposed to the scopes it merely
/// advertises support for in .well-known/smart-configuration). The signing key is resolved from the secret store by
/// reference — the raw PEM is never sent from or held by the browser.
/// </summary>
public sealed class BackendAuthScopeProbeService : IBackendAuthScopeProbeService
{
    // Epic's system scope grammar has no literal wildcard access-level — 'system/*.*' is rejected as invalid_scope.
    // The access level must be a real suffix ('.read' for v1, '.rs' for v2); this fallback only applies when the
    // caller didn't specify one (the wizard always does, per its own detected scope version).
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
        string privateKeyPem;
        try
        {
            privateKeyPem = await _secretProvider.GetSecretAsync(
                new SecretReference(request.PrivateKeyVaultName, request.PrivateKeySecretName),
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

        var scope = string.IsNullOrWhiteSpace(request.Scope) ? DefaultScope : request.Scope;
        var httpClient = _httpClientFactory.CreateClient(nameof(BackendAuthScopeProbeService));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = request.ClientId,
                ["scope"] = scope,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = clientAssertion,
            }),
        };

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = $"Epic token endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}).";
                message= string.Format(
    "Epic backend-services token exchange failed for client {0} against {1} with scope '{2}': {3} {4} — {5}",
    request.ClientId,
    request.TokenEndpoint,
    scope,
    (int)response.StatusCode,
    response.ReasonPhrase,
    body);
                // The client-facing message is sanitized (an arbitrary external response could contain markup or an
                // oversized payload) — log the raw body here so a real invalid_scope/invalid_client reason is still
                // diagnosable in Seq without exposing it to the browser. No token/JWT/key material is ever in this
                // body; it's Epic's own OAuth error response.
                _logger.LogWarning(
                    "Epic backend-services token exchange failed for client {ClientId} against {TokenEndpoint} " +
                    "with scope '{Scope}': {StatusCode} {ReasonPhrase} — {Body}",
                    request.ClientId, request.TokenEndpoint, scope, (int)response.StatusCode, response.ReasonPhrase, body);
                return new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(body, message) ?? message);
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return new BackendAuthScopesResult(false, [], "Epic token endpoint returned an empty access token.");
            }

            var grantedScopes = string.IsNullOrWhiteSpace(tokenResponse.Scope)
                ? []
                : tokenResponse.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new BackendAuthScopesResult(true, grantedScopes, null);
        }
        catch (Exception ex)
        {
            return new BackendAuthScopesResult(false, [], SafeErrorText.SanitizeOr(
                ex.Message, "Could not reach the Epic token endpoint."));
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string? Scope);
}
