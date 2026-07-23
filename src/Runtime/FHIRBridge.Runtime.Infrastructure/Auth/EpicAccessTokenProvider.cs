using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

public sealed class EpicAccessTokenProvider : IFhirAccessTokenProvider
{
    private readonly HttpClient _httpClient;
    private readonly IBackendServicesJwtFactory _jwtFactory;
    private readonly IFhirAccessTokenAuditSink _auditSink;
    private readonly IFhirAccessTokenCache _tokenCache;

    public EpicAccessTokenProvider(
        HttpClient httpClient,
        IBackendServicesJwtFactory jwtFactory,
        IFhirAccessTokenAuditSink? auditSink = null,
        IFhirAccessTokenCache? tokenCache = null)
    {
        _httpClient = httpClient;
        _jwtFactory = jwtFactory;
        _auditSink = auditSink ?? new NoOpFhirAccessTokenAuditSink();
        _tokenCache = tokenCache ?? new InMemoryFhirAccessTokenCache();
    }

    public async Task<string> GetAccessTokenAsync(
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        ValidateSource(source);

        var scopes = source.Scopes.Count == 0 ? "system/*.read" : string.Join(' ', source.Scopes);
        var cacheKey = $"fhir-token:epic|{source.TokenEndpoint}|{source.ClientId}|{scopes}";

        var cachedToken = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cachedToken is not null)
        {
            return cachedToken;
        }

        var clientAssertion = _jwtFactory.CreateClientAssertion(new BackendServicesJwtRequest(
            source.ClientId!,
            source.TokenEndpoint!,
            source.PrivateKeyPem!,
            source.KeyId,
            TimeSpan.FromMinutes(5)));

        using var request = new HttpRequestMessage(HttpMethod.Post, source.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = source.ClientId!,
                ["scope"] = scopes,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = clientAssertion
            })
        };
        TokenResponse tokenResponse;
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var message = await BuildFailureMessageAsync(
                        "Epic token endpoint",
                        response,
                        cancellationToken);

                    await _auditSink.RecordAsync(
                        source,
                        "EpicTokenRequestFailed",
                        "Failed",
                        message,
                        cancellationToken);

                    throw new InvalidOperationException(message);
                }

                tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
                if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                {
                    await _auditSink.RecordAsync(
                        source,
                        "EpicTokenRequestFailed",
                        "Failed",
                        "Epic token endpoint returned an empty access token.",
                        cancellationToken);

                    throw new InvalidOperationException("Epic token endpoint returned an empty access token.");
                }

                var expiresIn = tokenResponse.ExpiresIn <= 0 ? 300 : tokenResponse.ExpiresIn;
                await _tokenCache.SetAsync(
                    cacheKey,
                    tokenResponse.AccessToken,
                    DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                    cancellationToken);

                await _auditSink.RecordAsync(
                    source,
                    "EpicTokenRequestSucceeded",
                    "Completed",
                    "Epic access token acquired.",
                    cancellationToken);

                return tokenResponse.AccessToken;
            }
        }
        catch (Exception exception)
        {
            await _auditSink.RecordAsync(
                source,
                "EpicTokenRequestFailed",
                "Failed",
                exception.Message,
                cancellationToken);

            throw;
        }


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

    private static async Task<string> BuildFailureMessageAsync(
        string endpointName,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"{endpointName} returned {(int)response.StatusCode} ({response.ReasonPhrase}).";

        if (string.IsNullOrWhiteSpace(body))
        {
            return message;
        }

        body = body.ReplaceLineEndings(" ").Trim();
        if (body.Length > 1000)
        {
            body = body[..1000] + "...";
        }

        return $"{message} Response body: {body}";
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
}
