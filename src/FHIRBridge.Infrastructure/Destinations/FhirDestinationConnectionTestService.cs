using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Destinations.Auth;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests an ad-hoc FHIR-repository connection (the destination wizard's Test Connection button, before anything is
/// saved) by resolving an auth header directly from the request's own credential fields — never a stored secret,
/// since nothing has been persisted yet — then doing <c>GET {baseUrl}/metadata</c>, the same real FHIR
/// CapabilityStatement check <see cref="FhirRepositoryHealthCheckProvider"/> uses for an already-saved destination.
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

    public FhirDestinationConnectionTestService(
        IHttpClientFactory httpClientFactory,
        IFhirDestinationTokenProvider tokenProvider)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
    }

    public async Task<FhirConnectionTestResultDto> TestConnectionAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return new FhirConnectionTestResultDto(false, "Base URL is required.", null);
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
            return response.IsSuccessStatusCode
                ? new FhirConnectionTestResultDto(true, null, resolvedTokenEndpoint)
                : new FhirConnectionTestResultDto(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null);
        }
        catch (Exception exception)
        {
            return new FhirConnectionTestResultDto(false, exception.Message, null);
        }
    }

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
            default:
                throw new InvalidOperationException($"Unsupported auth type '{request.AuthType}'.");
        }
    }

    private async Task<(AuthenticationHeaderValue Header, string ResolvedTokenEndpoint)> ResolveClientCredentialsHeaderAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            throw new InvalidOperationException("Client ID and client secret are required.");
        }

        var tokenEndpoint = await DiscoverTokenEndpointAsync(request.BaseUrl, cancellationToken);

        var token = await _tokenProvider.GetAccessTokenAsync(
            new FhirDestinationOAuth2Options(tokenEndpoint, request.ClientId, request.ClientSecret, Scope: null),
            cancellationToken);
        return (new AuthenticationHeaderValue("Bearer", token), tokenEndpoint);
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
