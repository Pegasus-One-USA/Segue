using System.Net.Http.Headers;
using System.Text.Json;
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

    public async Task<SourceEndpointCapabilities> ProbeCapabilitiesAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var url = $"{NormalizeBaseUrl(baseUrl)}/metadata";
        var json = await GetAsync(url, "application/fhir+json", cancellationToken);
        var (_, resources) = SourceCapabilityDiscoveryService.ParseCapabilityStatement(json);

        var resourceTypes = resources
            .Where(resource => resource.Interactions.Any(interaction => ReadInteractions.Contains(interaction)))
            .Select(resource => resource.ResourceType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SourceEndpointCapabilities(resourceTypes, ParseSearchParameters(json));
    }

    /// <summary>
    /// Each resource type's declared search parameter names (<c>rest[].resource[].searchParam[].name</c>), with any
    /// <c>rest[].searchParam</c> declared for every type folded in. A type declaring none is left out: a statement
    /// that simply doesn't enumerate search parameters says nothing about what the server accepts.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseSearchParameters(string json)
    {
        var byResourceType = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("rest", out var restArray) || restArray.ValueKind != JsonValueKind.Array)
        {
            return byResourceType;
        }

        foreach (var rest in restArray.EnumerateArray())
        {
            var common = ReadSearchParamNames(rest);
            if (!rest.TryGetProperty("resource", out var resourceArray) || resourceArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var resource in resourceArray.EnumerateArray())
            {
                if (!resource.TryGetProperty("type", out var typeElement) || typeElement.GetString() is not { Length: > 0 } type)
                {
                    continue;
                }

                var names = ReadSearchParamNames(resource);
                if (names.Count == 0)
                {
                    continue;
                }

                byResourceType[type] = names.Concat(common).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        return byResourceType;
    }

    private static List<string> ReadSearchParamNames(JsonElement owner)
    {
        var names = new List<string>();
        if (owner.TryGetProperty("searchParam", out var searchParams) && searchParams.ValueKind == JsonValueKind.Array)
        {
            foreach (var searchParam in searchParams.EnumerateArray())
            {
                if (searchParam.TryGetProperty("name", out var nameElement) && nameElement.GetString() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
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
