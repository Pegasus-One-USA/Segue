using System.Net.Http.Headers;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Base-URL-only probe used by the source-connection wizard before a source is persisted. Reuses the SMART/metadata
/// parsers from <see cref="SourceCapabilityDiscoveryService"/> and the same named <see cref="HttpClient"/> (so global
/// retry/timeout defaults apply). Both endpoints are public per the SMART App Launch spec, so no token is sent.
/// </summary>
public sealed class SourceEndpointProbeService : ISourceEndpointProbeService
{
    private static readonly string[] ReadInteractions = ["read", "search-type", "search"];

    private readonly IHttpClientFactory _httpClientFactory;

    public SourceEndpointProbeService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<SmartConfigurationDto> ProbeSmartConfigurationAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var url = $"{NormalizeBaseUrl(baseUrl)}/.well-known/smart-configuration";
        var json = await GetAsync(url, "application/json", cancellationToken);
        return SourceCapabilityDiscoveryService.ParseSmartConfiguration(json);
    }

    public async Task<IReadOnlyList<string>> ProbeSupportedResourceTypesAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var url = $"{NormalizeBaseUrl(baseUrl)}/metadata";
        var json = await GetAsync(url, "application/fhir+json", cancellationToken);
        var (_, resources) = SourceCapabilityDiscoveryService.ParseCapabilityStatement(json);

        return resources
            .Where(resource => resource.Interactions.Any(interaction => ReadInteractions.Contains(interaction)))
            .Select(resource => resource.ResourceType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> GetAsync(string url, string accept, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(SourceCapabilityDiscoveryService));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Endpoint {url} returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("A valid absolute http(s) base URL is required.");
        }

        return baseUrl.TrimEnd('/');
    }
}
