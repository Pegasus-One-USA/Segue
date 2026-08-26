using System.Net;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology;

public sealed class FhirTerminologyLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<FhirTerminologyLookupService> _logger;

    public FhirTerminologyLookupService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<FhirTerminologyLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    public async Task<TerminologyLookupResult?> LookupAsync(
        string system,
        string code,
        CancellationToken cancellationToken)
    {
        // Admin-configurable via System Settings ("Terminology:BaseUrl"), falling back to appsettings/env
        // when no override has been set.
        var baseUrl = await _settings.GetStringAsync(
            "Terminology:BaseUrl", _configuration["Terminology:BaseUrl"] ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(system)
            || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var requestUrl = BuildLookupUrl(baseUrl, system, code);
        var client = _httpClientFactory.CreateClient(nameof(FhirTerminologyLookupService));

        try
        {
            using var response = await client.GetAsync(requestUrl, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var display = FindParameterValue(document.RootElement, "display");
            if (string.IsNullOrWhiteSpace(display))
            {
                return null;
            }

            return new TerminologyLookupResult(
                system.Trim(),
                code.Trim(),
                display,
                FindParameterValue(document.RootElement, "version"),
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
                "FHIR terminology lookup failed for system {System} and code {Code}.",
                system,
                code);

            return null;
        }
    }

    private static string BuildLookupUrl(string baseUrl, string system, string code)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        return $"{normalizedBaseUrl}/CodeSystem/$lookup?system={Uri.EscapeDataString(system.Trim())}&code={Uri.EscapeDataString(code.Trim())}";
    }

    private static string? FindParameterValue(JsonElement root, string parameterName)
    {
        if (!root.TryGetProperty("parameter", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var parameter in parameters.EnumerateArray())
        {
            if (!parameter.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), parameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parameter.TryGetProperty("valueString", out var valueString))
            {
                return valueString.GetString();
            }

            if (parameter.TryGetProperty("valueUri", out var valueUri))
            {
                return valueUri.GetString();
            }

            if (parameter.TryGetProperty("valueCode", out var valueCode))
            {
                return valueCode.GetString();
            }
        }

        return null;
    }
}
