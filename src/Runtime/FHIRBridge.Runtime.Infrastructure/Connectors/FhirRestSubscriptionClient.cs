using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// Registers and manages rest-hook <c>Subscription</c> resources on a FHIR R4 source server. Builds the Subscription
/// payload pointing at FHIRBridge's webhook ingestion URL, authenticates with the same access-token provider used for
/// search, and parses the server's response for the assigned id and status.
/// </summary>
public sealed class FhirRestSubscriptionClient : IFhirSubscriptionClient
{
    private readonly HttpClient _httpClient;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly ILogger<FhirRestSubscriptionClient> _logger;

    public FhirRestSubscriptionClient(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        ILogger<FhirRestSubscriptionClient>? logger = null)
    {
        _httpClient = httpClient;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger ?? NullLogger<FhirRestSubscriptionClient>.Instance;
    }

    public async Task<FhirSubscriptionRegistration> CreateAsync(
        FhirSubscriptionRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var baseUrl = RequireBaseUrl(source);
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        var body = BuildSubscriptionJson(request, subscriptionId: null);

        using var httpRequest = CreateRequest(HttpMethod.Post, $"{baseUrl}/Subscription", accessToken, body);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, responseBody, "create");

        // Prefer the returned resource; fall back to the Location header when the server returns no body.
        var registration = TryParseRegistration(responseBody)
            ?? new FhirSubscriptionRegistration(
                ExtractIdFromLocation(response.Headers.Location?.ToString()) ?? string.Empty,
                "requested",
                responseBody);

        _logger.LogInformation(
            "Created FHIR Subscription {SubscriptionId} (status {Status}) on {BaseUrl} for criteria {Criteria}.",
            registration.Id, registration.Status, baseUrl, request.Criteria);

        return registration;
    }

    public async Task<FhirSubscriptionRegistration> UpdateAsync(
        string subscriptionId,
        FhirSubscriptionRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("Subscription id is required.", nameof(subscriptionId));
        }

        var baseUrl = RequireBaseUrl(source);
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        var body = BuildSubscriptionJson(request, subscriptionId);

        using var httpRequest = CreateRequest(HttpMethod.Put, $"{baseUrl}/Subscription/{subscriptionId}", accessToken, body);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, responseBody, "update");

        return TryParseRegistration(responseBody)
            ?? new FhirSubscriptionRegistration(subscriptionId, "requested", responseBody);
    }

    public async Task DeleteAsync(
        string subscriptionId,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("Subscription id is required.", nameof(subscriptionId));
        }

        var baseUrl = RequireBaseUrl(source);
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);

        using var httpRequest = CreateRequest(HttpMethod.Delete, $"{baseUrl}/Subscription/{subscriptionId}", accessToken, body: null);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, responseBody, "delete");

        _logger.LogInformation("Deleted FHIR Subscription {SubscriptionId} on {BaseUrl}.", subscriptionId, baseUrl);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string accessToken, string? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/fhir+json");
        }

        return request;
    }

    private static string BuildSubscriptionJson(FhirSubscriptionRequest request, string? subscriptionId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Subscription");
            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                writer.WriteString("id", subscriptionId);
            }

            writer.WriteString("status", "requested");
            writer.WriteString("reason", string.IsNullOrWhiteSpace(request.Reason)
                ? "FHIRBridge change-data-capture subscription"
                : request.Reason);
            writer.WriteString("criteria", request.Criteria);

            writer.WritePropertyName("channel");
            writer.WriteStartObject();
            writer.WriteString("type", "rest-hook");
            writer.WriteString("endpoint", request.CallbackUrl);
            writer.WriteString("payload", request.PayloadMimeType);

            if (request.Headers is { Count: > 0 })
            {
                writer.WritePropertyName("header");
                writer.WriteStartArray();
                foreach (var header in request.Headers)
                {
                    writer.WriteStringValue(header);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject(); // channel
            writer.WriteEndObject(); // root
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static FhirSubscriptionRegistration? TryParseRegistration(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(GetString(root, "resourceType"), "Subscription", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var id = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return new FhirSubscriptionRegistration(
                id,
                GetString(root, "status") ?? "requested",
                responseBody);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractIdFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        // .../Subscription/{id}  or  .../Subscription/{id}/_history/{version}
        var segments = location.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "Subscription", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    private static string RequireBaseUrl(FhirSourceConfiguration source)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            throw new InvalidOperationException("FHIR source base URL is required to manage subscriptions.");
        }

        return source.BaseUrl.TrimEnd('/');
    }

    private static void EnsureSuccess(HttpResponseMessage response, string responseBody, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = responseBody.ReplaceLineEndings(" ").Trim();
        if (detail.Length > 1000)
        {
            detail = detail[..1000] + "...";
        }

        throw new InvalidOperationException(
            $"FHIR Subscription {operation} failed with {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}");
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
