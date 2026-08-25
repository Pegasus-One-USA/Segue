using System.Net.Http.Headers;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests an ad-hoc Medplum connection before anything is saved: builds the client credential straight from the
/// request (symmetric <c>client_secret</c> or SMART Backend Services <c>private_key_jwt</c>), mints an OAuth2 token
/// via <see cref="IMedplumTokenProvider"/> against the token endpoint (derived from the FHIR base URL, same rule
/// the writer uses — see <see cref="MedplumConnectionMetadata.ResolveTokenUrl"/>), then does a real
/// <c>GET {baseUrl}/metadata</c> with that bearer token. Never throws for connection failures; returns
/// <c>Connected=false</c> + <c>Error</c> instead, matching the FHIR/SFTP test contracts.
/// </summary>
public sealed class MedplumDestinationConnectionTestService : IMedplumDestinationConnectionTestService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMedplumTokenProvider _tokenProvider;

    public MedplumDestinationConnectionTestService(
        IHttpClientFactory httpClientFactory,
        IMedplumTokenProvider tokenProvider)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
    }

    public async Task<ConnectionTestResultDto> TestConnectionAsync(
        MedplumConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return new ConnectionTestResultDto(false, "FHIR base URL is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.Secret))
        {
            return new ConnectionTestResultDto(false, "Client ID and a client secret / private key are required.");
        }

        string token;
        try
        {
            var tokenUrl = new MedplumConnectionMetadata { TokenUrl = request.TokenUrl }.ResolveTokenUrl(request.BaseUrl);
            MedplumClientCredential credential =
                string.Equals(request.AuthMethod, "private_key_jwt", StringComparison.OrdinalIgnoreCase)
                    ? new MedplumPrivateKeyJwtCredential(request.ClientId, request.Secret, request.KeyId)
                    : new MedplumClientSecretCredential(request.ClientId, request.Secret);

            token = await _tokenProvider.GetAccessTokenAsync(tokenUrl, credential, cancellationToken);
        }
        catch (Exception exception)
        {
            // Auth-stage failure (bad client id/secret, unreachable token endpoint, malformed key, ...).
            return new ConnectionTestResultDto(false, $"Authentication failed: {exception.Message}");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(MedplumDestinationConnectionTestService));
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{request.BaseUrl.TrimEnd('/')}/metadata");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
}
