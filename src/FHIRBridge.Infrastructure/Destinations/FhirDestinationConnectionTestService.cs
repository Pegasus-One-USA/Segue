using System.Net.Http.Headers;
using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Destinations.Auth;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests an ad-hoc FHIR-repository connection (the destination wizard's Test Connection button, before anything is
/// saved) by resolving an auth header directly from the request's own credential fields — never a stored secret,
/// since nothing has been persisted yet — then doing <c>GET {baseUrl}/metadata</c>, the same real FHIR
/// CapabilityStatement check <see cref="FhirRepositoryHealthCheckProvider"/> uses for an already-saved destination.
/// Never throws for connection failures; returns <c>Connected=false</c> + <c>Error</c> instead, matching
/// <see cref="ICsvDestinationConnectionTestService"/>'s SFTP test contract.
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

    public async Task<ConnectionTestResultDto> TestConnectionAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return new ConnectionTestResultDto(false, "Base URL is required.");
        }

        AuthenticationHeaderValue? authHeader;
        try
        {
            authHeader = await ResolveAuthHeaderAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            return new ConnectionTestResultDto(false, $"Could not resolve credentials: {exception.Message}");
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
                ? new ConnectionTestResultDto(true, null)
                : new ConnectionTestResultDto(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception exception)
        {
            return new ConnectionTestResultDto(false, exception.Message);
        }
    }

    private async Task<AuthenticationHeaderValue?> ResolveAuthHeaderAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        return request.AuthType.ToLowerInvariant() switch
        {
            "none" or "" => null,
            "bearer" => string.IsNullOrWhiteSpace(request.BearerToken)
                ? throw new InvalidOperationException("Bearer token is required.")
                : new AuthenticationHeaderValue("Bearer", request.BearerToken),
            "basic" => string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password)
                ? throw new InvalidOperationException("Username and password are required.")
                : new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"))),
            "clientcredentials" or "oauth2" => await ResolveClientCredentialsHeaderAsync(request, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported auth type '{request.AuthType}'."),
        };
    }

    private async Task<AuthenticationHeaderValue> ResolveClientCredentialsHeaderAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TokenEndpoint)
            || string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            throw new InvalidOperationException("Token endpoint, client ID, and client secret are required.");
        }

        var token = await _tokenProvider.GetAccessTokenAsync(
            new FhirDestinationOAuth2Options(request.TokenEndpoint, request.ClientId, request.ClientSecret, Scope: null),
            cancellationToken);
        return new AuthenticationHeaderValue("Bearer", token);
    }
}
