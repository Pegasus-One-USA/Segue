using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests an ad-hoc FHIR-repository connection (the destination wizard's Test Connection button, before anything is
/// saved) by resolving an auth header directly from the request's own credential fields — never a stored secret,
/// since nothing has been persisted yet — then doing <c>GET {baseUrl}/metadata</c>, the same real FHIR
/// CapabilityStatement check <see cref="FhirRepositoryHealthCheckProvider"/> uses for an already-saved destination,
/// followed by a real write-authorization probe (see <see cref="ProbeWriteAuthorizationAsync"/>).
/// For "clientcredentials"/"oauth2", the token endpoint isn't supplied by the caller — it's discovered from
/// <c>BaseUrl</c> via the SMART App Launch <c>.well-known/smart-configuration</c> document (see
/// <see cref="DiscoverTokenEndpointAsync"/>), and the discovered value is returned to the caller so the wizard can
/// persist it exactly as if the user had typed it in. Never throws for connection failures; returns
/// <c>Connected=false</c> + <c>Error</c> instead, matching <see cref="ICsvDestinationConnectionTestService"/>'s
/// SFTP test contract.
/// </summary>
public sealed class FhirDestinationConnectionTestService : IFhirDestinationConnectionTestService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFhirDestinationTokenProvider _tokenProvider;
    private readonly IAzureManagedIdentityFhirTokenProvider _managedIdentityTokenProvider;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;

    public FhirDestinationConnectionTestService(
        IHttpClientFactory httpClientFactory,
        IFhirDestinationTokenProvider tokenProvider,
        IAzureManagedIdentityFhirTokenProvider managedIdentityTokenProvider,
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _managedIdentityTokenProvider = managedIdentityTokenProvider;
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
    }

    public async Task<FhirConnectionTestResultDto> TestConnectionAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return new FhirConnectionTestResultDto(false, "Base URL is required.", null);
        }

        // Re-testing an already-saved destination: the form never re-displays the stored secret, so a blank
        // ClientSecret/Password/BearerToken here means "use what's already saved" — resolve just that field from
        // the vault instead (mirrors MongoDestinationConnectionTestService's identical DestinationId fallback).
        // ClientId/Username aren't secret (they round-trip via ConnectionMetadataJson, already current in the
        // form), so only the actual secret field is filled in — never the whole request overwritten.
        if (request.DestinationId is { } destinationId && NeedsResolvedSecret(request))
        {
            var resolved = await ResolveStoredSecretAsync(destinationId, request.AuthType, cancellationToken);
            if (resolved is not null)
            {
                request = request.AuthType.ToLowerInvariant() switch
                {
                    "bearer" => request with { BearerToken = resolved.Token },
                    "basic" => request with { Password = resolved.Password },
                    "clientcredentials" or "oauth2" => request with { ClientSecret = resolved.ClientSecret },
                    _ => request,
                };
            }
        }

        AuthenticationHeaderValue? authHeader;
        string? resolvedTokenEndpoint;
        try
        {
            (authHeader, resolvedTokenEndpoint) = await ResolveAuthHeaderAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            return new FhirConnectionTestResultDto(false, exception.Message, null);
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(FhirDestinationConnectionTestService));
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{request.BaseUrl.TrimEnd('/')}/metadata");
            if (authHeader is not null)
            {
                httpRequest.Headers.Authorization = authHeader;
            }

            using var response = await client.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new FhirConnectionTestResultDto(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null);
            }
        }
        catch (Exception exception)
        {
            return new FhirConnectionTestResultDto(false, exception.Message, null);
        }

        // A successful GET /metadata only proves the token/tenant/audience are valid — Azure Health Data
        // Services (and many FHIR servers generally) don't enforce write-level RBAC on the capability
        // statement, so a principal with NO write role at all can still pass the check above and then fail
        // the very first real pipeline write with 403 "Authorization failed". Only an actual write attempt
        // can confirm write authorization really works — so make one, against a synthetic, self-cleaning
        // probe resource, and surface any failure here instead of at the first real run.
        var writeCheckError = await ProbeWriteAuthorizationAsync(request.BaseUrl, authHeader, cancellationToken);
        return writeCheckError is null
            ? new FhirConnectionTestResultDto(true, null, resolvedTokenEndpoint)
            : new FhirConnectionTestResultDto(false, writeCheckError, null);
    }

    /// <summary>Whether this request's auth type actually needs a secret at all, and the caller left it blank —
    /// the signal that DestinationId's stored value should be resolved rather than failing outright. Managed
    /// identity/none never need a secret regardless of DestinationId.</summary>
    private static bool NeedsResolvedSecret(FhirConnectionTestRequest request) =>
        request.AuthType.ToLowerInvariant() switch
        {
            "bearer" => string.IsNullOrWhiteSpace(request.BearerToken),
            "basic" => string.IsNullOrWhiteSpace(request.Password),
            "clientcredentials" or "oauth2" => string.IsNullOrWhiteSpace(request.ClientSecret),
            _ => false,
        };

    /// <summary>Resolves and parses the already-saved destination's Key Vault secret — reuses
    /// FhirRepositoryAuthResolver's own secret-blob shape/parser so this never drifts out of sync with what a
    /// saved FhirRepository/AzureFhirService destination's secret actually looks like. Returns null (falls back
    /// to whatever blank value the caller sent, which then fails validation with a clear message) when the
    /// destination doesn't exist, has no secret configured, or its stored secret doesn't parse for this auth
    /// type — never throws, matching this service's "never throws for connection failures" contract.</summary>
    private async Task<FhirRepositoryAuthResolver.FhirRepositoryAuthSecret?> ResolveStoredSecretAsync(
        Guid destinationId, string authType, CancellationToken cancellationToken)
    {
        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        if (destination is null)
        {
            return null;
        }

        try
        {
            var secretJson = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
            return FhirRepositoryAuthResolver.FhirRepositoryAuthSecret.Parse(secretJson, authType);
        }
        catch (SecretNotConfiguredException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // Stored secret doesn't parse as JSON, or is shaped for a different auth type than requested —
            // fall back to the blank value the caller sent rather than surfacing a confusing parse error for
            // what the user experiences as "I left the secret blank to reuse it."
            return null;
        }
    }

    // Fixed id (not a fresh Guid per call) so repeated tests overwrite/clean up the same one resource rather
    // than accumulating garbage if a delete ever fails. FHIR's Basic resource type is deliberately used —
    // it carries no semantic meaning and no server-side profile/required-field validation, so a failure here
    // can only mean an authorization or connectivity problem, never "the probe body itself was invalid" (which
    // would otherwise be indistinguishable from a real authorization failure to the user).
    private const string ProbeResourceId = "fhirbridge-connection-test-probe";
    private static readonly string ProbeResourceBody =
        "{\"resourceType\":\"Basic\",\"id\":\"" + ProbeResourceId + "\",\"code\":{\"text\":\"FHIRBridge connection test probe — safe to delete\"}}";

    /// <summary>
    /// Confirms the resolved credentials can actually WRITE, not just read the capability statement — PUTs a
    /// synthetic <c>Basic</c> resource (see <see cref="ProbeResourceId"/>) and, on success, immediately deletes
    /// it again (best-effort; a leftover probe resource is harmless and gets overwritten/cleaned up by the next
    /// test either way). Returns null on success, or a user-facing error message mirroring
    /// <see cref="MappedFhirRepositoryDestinationWriter"/>'s own write-failure message shape so this looks like
    /// the same class of error the user would otherwise only see after a real pipeline run failed.
    /// </summary>
    private async Task<string?> ProbeWriteAuthorizationAsync(
        string baseUrl, AuthenticationHeaderValue? authHeader, CancellationToken cancellationToken)
    {
        var endpoint = $"{baseUrl.TrimEnd('/')}/Basic/{ProbeResourceId}";
        var client = _httpClientFactory.CreateClient(nameof(FhirDestinationConnectionTestService));

        try
        {
            using var putRequest = new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = new StringContent(ProbeResourceBody, Encoding.UTF8, "application/fhir+json"),
            };
            if (authHeader is not null)
            {
                putRequest.Headers.Authorization = authHeader;
            }

            using var putResponse = await client.SendAsync(putRequest, cancellationToken);
            if (!putResponse.IsSuccessStatusCode)
            {
                var body = await putResponse.Content.ReadAsStringAsync(cancellationToken);
                return $"Write check failed — PUT '{endpoint}' returned {(int)putResponse.StatusCode} ({putResponse.ReasonPhrase}): {Truncate(body, 500)}";
            }
        }
        catch (Exception exception)
        {
            return $"Write check failed: {exception.Message}";
        }

        try
        {
            using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            if (authHeader is not null)
            {
                deleteRequest.Headers.Authorization = authHeader;
            }

            using var deleteResponse = await client.SendAsync(deleteRequest, cancellationToken);
            _ = deleteResponse; // best-effort cleanup — a failed delete here doesn't invalidate the write check
        }
        catch
        {
            // best-effort cleanup only — the write itself already succeeded, which is what this probe confirms
        }

        return null;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private async Task<(AuthenticationHeaderValue? Header, string? ResolvedTokenEndpoint)> ResolveAuthHeaderAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.AuthType.ToLowerInvariant())
        {
            case "none" or "":
                return (null, null);
            case "bearer":
                return string.IsNullOrWhiteSpace(request.BearerToken)
                    ? throw new InvalidOperationException("Bearer token is required.")
                    : (new AuthenticationHeaderValue("Bearer", request.BearerToken), null);
            case "basic":
                if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                {
                    throw new InvalidOperationException("Username and password are required.");
                }
                return (
                    new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"))),
                    null);
            case "clientcredentials" or "oauth2":
                return await ResolveClientCredentialsHeaderAsync(request, cancellationToken);
            case "managedidentity":
                return await ResolveManagedIdentityHeaderAsync(request, cancellationToken);
            default:
                throw new InvalidOperationException($"Unsupported auth type '{request.AuthType}'.");
        }
    }

    private async Task<(AuthenticationHeaderValue Header, string? ResolvedTokenEndpoint)> ResolveClientCredentialsHeaderAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            throw new InvalidOperationException("Client ID and client secret are required.");
        }

        // Azure FHIR Service already knows its own tenant — no need to discover the token endpoint from a
        // SMART configuration document (which Azure's FHIR service may not even publish the same way Aidbox
        // does). Skip discovery entirely whenever the caller supplies TenantId.
        if (!string.IsNullOrWhiteSpace(request.TenantId))
        {
            var azureTokenEndpoint = $"https://login.microsoftonline.com/{request.TenantId}/oauth2/v2.0/token";
            var scope = string.IsNullOrWhiteSpace(request.Scope)
                ? $"{request.BaseUrl.TrimEnd('/')}/.default"
                : request.Scope;
            var azureToken = await _tokenProvider.GetAccessTokenAsync(
                new FhirDestinationOAuth2Options(azureTokenEndpoint, request.ClientId, request.ClientSecret, scope),
                cancellationToken);
            return (new AuthenticationHeaderValue("Bearer", azureToken), azureTokenEndpoint);
        }

        var tokenEndpoint = await DiscoverTokenEndpointAsync(request.BaseUrl, cancellationToken);

        var token = await _tokenProvider.GetAccessTokenAsync(
            new FhirDestinationOAuth2Options(tokenEndpoint, request.ClientId, request.ClientSecret, Scope: null),
            cancellationToken);
        return (new AuthenticationHeaderValue("Bearer", token), tokenEndpoint);
    }

    private async Task<(AuthenticationHeaderValue Header, string? ResolvedTokenEndpoint)> ResolveManagedIdentityHeaderAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(request.Scope)
            ? $"{request.BaseUrl.TrimEnd('/')}/.default"
            : request.Scope;
        var token = await _managedIdentityTokenProvider.GetAccessTokenAsync(
            scope, request.ManagedIdentityClientId, request.AuthorityHost, cancellationToken);
        return (new AuthenticationHeaderValue("Bearer", token), null);
    }

    /// <summary>
    /// Resolves the OAuth2 token endpoint from the SMART App Launch discovery document
    /// (<c>GET {baseUrl}/.well-known/smart-configuration</c>) rather than requiring the user to know/type it —
    /// Aidbox (and any SMART-conformant FHIR server) publishes it there. Throws a message that's clearly a
    /// discovery failure, not a credentials failure, so the two don't look the same in the UI.
    /// </summary>
    private async Task<string> DiscoverTokenEndpointAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var discoveryUrl = $"{baseUrl.TrimEnd('/')}/.well-known/smart-configuration";
        var client = _httpClientFactory.CreateClient(nameof(FhirDestinationConnectionTestService));

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(discoveryUrl, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not reach {discoveryUrl} to discover the token endpoint: {exception.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Could not discover the token endpoint from {discoveryUrl}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("token_endpoint", out var tokenEndpointElement)
                    && tokenEndpointElement.ValueKind == JsonValueKind.String)
                {
                    var tokenEndpoint = tokenEndpointElement.GetString();
                    if (!string.IsNullOrWhiteSpace(tokenEndpoint))
                    {
                        return tokenEndpoint;
                    }
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"{discoveryUrl} did not return valid JSON: {exception.Message}");
            }

            throw new InvalidOperationException($"{discoveryUrl} did not include a 'token_endpoint' field.");
        }
    }
}
