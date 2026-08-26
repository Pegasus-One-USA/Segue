using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Expands a ValueSet via a FHIR terminology server's <c>ValueSet/$expand</c> operation. Configured by the
/// "Terminology:BaseUrl" System Setting (falling back to <c>Terminology:BaseUrl</c> in appsettings); returns
/// empty when unset or on failure.
/// </summary>
public sealed class FhirTerminologyExpansionService : ITerminologyExpansionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<FhirTerminologyExpansionService> _logger;

    public FhirTerminologyExpansionService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<FhirTerminologyExpansionService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger ?? NullLogger<FhirTerminologyExpansionService>.Instance;
    }

    public async Task<IReadOnlyList<TerminologyConcept>> ExpandAsync(string valueSetUrl, CancellationToken cancellationToken)
    {
        var baseUrl = await _settings.GetStringAsync(
            "Terminology:BaseUrl", _configuration["Terminology:BaseUrl"] ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(valueSetUrl))
        {
            return [];
        }

        var requestUrl = $"{baseUrl.TrimEnd('/')}/ValueSet/$expand?url={Uri.EscapeDataString(valueSetUrl.Trim())}";
        var client = _httpClientFactory.CreateClient(nameof(FhirTerminologyExpansionService));

        try
        {
            using var response = await client.GetAsync(requestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return ParseExpansion(document.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "FHIR $expand failed for {ValueSet}.", valueSetUrl);
            return [];
        }
    }

    // Reads ValueSet.expansion.contains[] into concepts.
    private static IReadOnlyList<TerminologyConcept> ParseExpansion(JsonElement root)
    {
        if (!root.TryGetProperty("expansion", out var expansion) ||
            !expansion.TryGetProperty("contains", out var contains) ||
            contains.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var concepts = new List<TerminologyConcept>();
        foreach (var entry in contains.EnumerateArray())
        {
            var code = GetString(entry, "code");
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            concepts.Add(new TerminologyConcept(
                GetString(entry, "system") ?? string.Empty,
                code,
                GetString(entry, "display")));
        }

        return concepts;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
