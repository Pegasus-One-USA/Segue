using System.Net;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Translates codes via a FHIR terminology server's <c>ConceptMap/$translate</c> operation. Configured by
/// the "Terminology:BaseUrl" System Setting (falling back to <c>Terminology:BaseUrl</c> in appsettings);
/// returns null when unset or when no match is found.
/// </summary>
public sealed class FhirTerminologyTranslationService : ITerminologyTranslationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<FhirTerminologyTranslationService> _logger;

    public FhirTerminologyTranslationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<FhirTerminologyTranslationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    public async Task<TerminologyTranslationResult?> TranslateAsync(
        string sourceSystem,
        string sourceCode,
        string targetSystem,
        CancellationToken cancellationToken)
    {
        var baseUrl = await _settings.GetStringAsync(
            "Terminology:BaseUrl", _configuration["Terminology:BaseUrl"] ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(sourceSystem)
            || string.IsNullOrWhiteSpace(sourceCode)
            || string.IsNullOrWhiteSpace(targetSystem))
        {
            return null;
        }

        var requestUrl = $"{baseUrl.TrimEnd('/')}/ConceptMap/$translate"
            + $"?system={Uri.EscapeDataString(sourceSystem.Trim())}"
            + $"&code={Uri.EscapeDataString(sourceCode.Trim())}"
            + $"&targetsystem={Uri.EscapeDataString(targetSystem.Trim())}";
        var client = _httpClientFactory.CreateClient(nameof(FhirTerminologyTranslationService));

        try
        {
            using var response = await client.GetAsync(requestUrl, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var concept = FindFirstMatchConcept(document.RootElement);
            if (concept is null)
            {
                return null;
            }

            return new TerminologyTranslationResult(
                sourceSystem.Trim(),
                sourceCode.Trim(),
                targetSystem.Trim(),
                concept.Value.Code,
                concept.Value.Display,
                "FhirTerminology");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "FHIR terminology translate failed for {SourceSystem}|{Code} -> {TargetSystem}.",
                sourceSystem, sourceCode, targetSystem);

            return null;
        }
    }

    // Parses a $translate Parameters response: parameter[name=match] -> part[name=concept] -> valueCoding{system,code,display}.
    private static (string Code, string? Display)? FindFirstMatchConcept(JsonElement root)
    {
        if (!root.TryGetProperty("parameter", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var parameter in parameters.EnumerateArray())
        {
            if (!parameter.TryGetProperty("name", out var name) ||
                !string.Equals(name.GetString(), "match", StringComparison.OrdinalIgnoreCase) ||
                !parameter.TryGetProperty("part", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("name", out var partName) &&
                    string.Equals(partName.GetString(), "concept", StringComparison.OrdinalIgnoreCase) &&
                    part.TryGetProperty("valueCoding", out var coding) &&
                    coding.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String)
                {
                    var display = coding.TryGetProperty("display", out var displayElement) && displayElement.ValueKind == JsonValueKind.String
                        ? displayElement.GetString()
                        : null;

                    return (code.GetString()!, display);
                }
            }
        }

        return null;
    }
}
