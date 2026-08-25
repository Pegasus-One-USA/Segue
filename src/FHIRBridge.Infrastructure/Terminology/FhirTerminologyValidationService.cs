using System.Net;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Validates codes against a FHIR terminology server's <c>ValueSet/$validate-code</c> operation. Configured
/// by the "Terminology:BaseUrl" System Setting (falling back to <c>Terminology:BaseUrl</c> in appsettings);
/// returns null when unset or when the result cannot be determined.
/// </summary>
public sealed class FhirTerminologyValidationService : ITerminologyValidationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<FhirTerminologyValidationService> _logger;

    public FhirTerminologyValidationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemSettingsCache settings,
        ILogger<FhirTerminologyValidationService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger ?? NullLogger<FhirTerminologyValidationService>.Instance;
    }

    public async Task<TerminologyValidationResult?> ValidateCodeAsync(
        string valueSetUrl,
        string? system,
        string code,
        CancellationToken cancellationToken)
    {
        var baseUrl = await _settings.GetStringAsync(
            "Terminology:BaseUrl", _configuration["Terminology:BaseUrl"] ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(valueSetUrl) || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var requestUrl = $"{baseUrl.TrimEnd('/')}/ValueSet/$validate-code"
            + $"?url={Uri.EscapeDataString(valueSetUrl.Trim())}"
            + $"&code={Uri.EscapeDataString(code.Trim())}";
        if (!string.IsNullOrWhiteSpace(system))
        {
            requestUrl += $"&system={Uri.EscapeDataString(system.Trim())}";
        }

        var client = _httpClientFactory.CreateClient(nameof(FhirTerminologyValidationService));

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

            var result = ReadResultParameter(document.RootElement);
            if (result is null)
            {
                return null;
            }

            return new TerminologyValidationResult(
                result.Value,
                result.Value ? null : $"Code '{code}' is not a member of {valueSetUrl}.",
                "FhirTerminology");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "FHIR $validate-code failed for {Code} against {ValueSet}.", code, valueSetUrl);
            return null;
        }
    }

    // Reads the boolean "result" parameter from a $validate-code Parameters response.
    private static bool? ReadResultParameter(JsonElement root)
    {
        if (!root.TryGetProperty("parameter", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), "result", StringComparison.OrdinalIgnoreCase) &&
                parameter.TryGetProperty("valueBoolean", out var value) &&
                (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            {
                return value.GetBoolean();
            }
        }

        return null;
    }
}
