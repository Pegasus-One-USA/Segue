using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Normalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Normalization;

/// <summary>
/// Calls a FHIR server's <c>Patient/$match</c> operation to resolve a patient to a master identity. Configured by
/// <c>PatientMatch:BaseUrl</c>; disabled (and null-returning) when unset. Wraps the incoming Patient in a
/// <c>Parameters</c> resource, posts it, and returns the highest-scoring match's Patient id from the result Bundle.
/// </summary>
public sealed class FhirPatientMatchService : IPatientMatchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FhirPatientMatchService> _logger;

    public FhirPatientMatchService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FhirPatientMatchService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger ?? NullLogger<FhirPatientMatchService>.Instance;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_configuration["PatientMatch:BaseUrl"]);

    public async Task<string?> MatchAsync(string patientResourceJson, CancellationToken cancellationToken)
    {
        var baseUrl = _configuration["PatientMatch:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var parameters = BuildMatchParameters(patientResourceJson);
        var client = _httpClientFactory.CreateClient(nameof(FhirPatientMatchService));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/Patient/$match")
        {
            Content = new StringContent(parameters, Encoding.UTF8, "application/fhir+json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseBestMatchId(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Patient $match call failed; falling back to deterministic matching.");
            return null;
        }
    }

    private static string BuildMatchParameters(string patientResourceJson)
    {
        using var patient = JsonDocument.Parse(patientResourceJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Parameters");
            writer.WritePropertyName("parameter");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("name", "resource");
            writer.WritePropertyName("resource");
            patient.RootElement.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The $match result is a searchset Bundle; pick the entry with the highest match score.
    private static string? ParseBestMatchId(string bundleJson)
    {
        using var document = JsonDocument.Parse(bundleJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? bestId = null;
        var bestScore = double.NegativeInfinity;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource) ||
                !string.Equals(GetString(resource, "resourceType"), "Patient", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = GetString(resource, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var score = ReadScore(entry);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        return bestId;
    }

    private static double ReadScore(JsonElement entry)
    {
        if (entry.TryGetProperty("search", out var search) &&
            search.TryGetProperty("score", out var score) &&
            score.ValueKind == JsonValueKind.Number)
        {
            return score.GetDouble();
        }

        return 0d;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
