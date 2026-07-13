using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// Implements the FHIR Bulk Data Access "ping-pong" flow: kick off <c>$export</c> with <c>Prefer: respond-async</c>,
/// poll the <c>Content-Location</c> status URL until the job completes (honoring <c>Retry-After</c>), then stream and
/// parse each NDJSON output file into <see cref="ResourceEnvelope"/> values.
/// </summary>
public sealed class FhirRestBulkExportClient : IFhirBulkExportClient
{
    private readonly HttpClient _httpClient;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly FhirBulkExportOptions _options;
    private readonly ILogger<FhirRestBulkExportClient> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FhirRestBulkExportClient(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        IOptions<FhirBulkExportOptions>? options = null,
        ILogger<FhirRestBulkExportClient>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient;
        _accessTokenProvider = accessTokenProvider;
        _options = options?.Value ?? new FhirBulkExportOptions();
        _logger = logger ?? NullLogger<FhirRestBulkExportClient>.Instance;
        _delay = delay ?? Task.Delay;
    }

    public async Task<IReadOnlyList<ResourceEnvelope>> ExportAsync(
        FhirBulkExportRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var baseUrl = RequireBaseUrl(source);
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);

        var statusUrl = await KickOffAsync(baseUrl, request, accessToken, cancellationToken);
        var files = await PollUntilCompleteAsync(statusUrl, accessToken, cancellationToken);

        var resources = new List<ResourceEnvelope>();
        foreach (var file in files)
        {
            resources.AddRange(await DownloadNdjsonAsync(file, accessToken, cancellationToken));
        }

        _logger.LogInformation(
            "Bulk export produced {ResourceCount} resources across {FileCount} NDJSON files.",
            resources.Count, files.Count);

        return resources;
    }

    private async Task<string> KickOffAsync(
        string baseUrl,
        FhirBulkExportRequest request,
        string accessToken,
        CancellationToken cancellationToken)
    {
        // A patient-scoped export narrowed to a specific id list can only be expressed as a POST with a `patient`
        // parameter in a Parameters resource body (FHIR Bulk Data v2) — it has no GET query-string form. Every other
        // case (system, group, all-patient) stays a GET kick-off.
        var usePostWithPatientList = request.Scope == BulkExportScope.Patient
            && request.PatientIds is { Count: > 0 };

        using var httpRequest = usePostWithPatientList
            ? BuildPostKickOff(baseUrl, request)
            : new HttpRequestMessage(HttpMethod.Get, BuildKickOffUrl(baseUrl, request));

        var kickOffUrl = httpRequest.RequestUri!.ToString();
        SetBearer(httpRequest, accessToken);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
        httpRequest.Headers.TryAddWithoutValidation("Prefer", "respond-async");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            var body = await SafeReadAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"Bulk export kick-off returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {kickOffUrl}. {body}");
        }

        var statusUrl = response.Headers.Location?.ToString()
            ?? (response.Content.Headers.ContentLocation?.ToString());
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            throw new InvalidOperationException("Bulk export kick-off did not return a Content-Location status URL.");
        }

        _logger.LogInformation("Bulk export kicked off; polling status at {StatusUrl}.", statusUrl);

        return statusUrl;
    }

    private async Task<IReadOnlyList<BulkExportFile>> PollUntilCompleteAsync(
        string statusUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.MaxPollAttempts; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            SetBearer(httpRequest, accessToken);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseManifest(body);
            }

            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                var body = await SafeReadAsync(response, cancellationToken);
                throw new InvalidOperationException(
                    $"Bulk export status poll returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
            }

            await _delay(ResolvePollDelay(response), cancellationToken);
        }

        throw new TimeoutException(
            $"Bulk export did not complete after {_options.MaxPollAttempts} status polls.");
    }

    private async Task<IReadOnlyList<ResourceEnvelope>> DownloadNdjsonAsync(
        BulkExportFile file,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, file.Url);
        SetBearer(httpRequest, accessToken);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+ndjson"));

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"Bulk export file download returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {file.Url}. {body}");
        }

        var resources = new List<ResourceEnvelope>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            resources.Add(FhirResourceParser.ParseResource(line));
        }

        return resources;
    }

    private TimeSpan ResolvePollDelay(HttpResponseMessage response)
    {
        var maximum = TimeSpan.FromSeconds(Math.Max(1, _options.MaxPollIntervalSeconds));

        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > maximum ? maximum : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var dateDelay = date - DateTimeOffset.UtcNow;
            if (dateDelay > TimeSpan.Zero)
            {
                return dateDelay > maximum ? maximum : dateDelay;
            }
        }

        return TimeSpan.FromSeconds(Math.Max(1, _options.DefaultPollIntervalSeconds));
    }

    private static IReadOnlyList<BulkExportFile> ParseManifest(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var files = new List<BulkExportFile>();
        foreach (var entry in output.EnumerateArray())
        {
            var url = GetString(entry, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            files.Add(new BulkExportFile(GetString(entry, "type") ?? "Resource", url));
        }

        return files;
    }

    private static string BuildKickOffUrl(string baseUrl, FhirBulkExportRequest request)
    {
        var path = request.Scope switch
        {
            BulkExportScope.System => "$export",
            BulkExportScope.Patient => "Patient/$export",
            BulkExportScope.Group => string.IsNullOrWhiteSpace(request.GroupId)
                ? throw new InvalidOperationException("Group bulk export requires a GroupId.")
                : $"Group/{request.GroupId}/$export",
            _ => "Patient/$export"
        };

        var query = new List<string>();
        if (request.ResourceTypes is { Count: > 0 })
        {
            query.Add($"_type={Uri.EscapeDataString(string.Join(',', request.ResourceTypes))}");
        }

        if (request.Since is { } since)
        {
            query.Add($"_since={Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}");
        }

        if (!string.IsNullOrWhiteSpace(request.TypeFilter))
        {
            query.Add($"_typeFilter={Uri.EscapeDataString(request.TypeFilter)}");
        }

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
        {
            query.Add($"_outputFormat={Uri.EscapeDataString(request.OutputFormat)}");
        }

        var url = $"{baseUrl}/{path}";
        return query.Count == 0 ? url : $"{url}?{string.Join('&', query)}";
    }

    // POST [base]/Patient/$export with a Parameters resource carrying the export params and a repeated `patient`
    // entry per id — the only way to scope an export to a specific patient list (FHIR Bulk Data v2, POST-only).
    private static HttpRequestMessage BuildPostKickOff(string baseUrl, FhirBulkExportRequest request)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Parameters");
            writer.WriteStartArray("parameter");

            if (request.OutputFormat is { Length: > 0 } outputFormat)
            {
                WriteStringParameter(writer, "_outputFormat", outputFormat);
            }

            if (request.Since is { } since)
            {
                WriteParameter(writer, "_since", "valueInstant", since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            }

            if (request.ResourceTypes is { Count: > 0 })
            {
                WriteStringParameter(writer, "_type", string.Join(',', request.ResourceTypes));
            }

            if (!string.IsNullOrWhiteSpace(request.TypeFilter))
            {
                WriteStringParameter(writer, "_typeFilter", request.TypeFilter);
            }

            foreach (var patientId in request.PatientIds!)
            {
                if (string.IsNullOrWhiteSpace(patientId))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("name", "patient");
                writer.WriteStartObject("valueReference");
                writer.WriteString("reference", patientId.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase)
                    ? patientId
                    : $"Patient/{patientId}");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/Patient/$export")
        {
            Content = new StringContent(Encoding.UTF8.GetString(buffer.ToArray()), Encoding.UTF8, "application/fhir+json"),
        };
        return httpRequest;
    }

    private static void WriteStringParameter(Utf8JsonWriter writer, string name, string value)
        => WriteParameter(writer, name, "valueString", value);

    private static void WriteParameter(Utf8JsonWriter writer, string name, string valueField, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString(valueField, value);
        writer.WriteEndObject();
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body = body.ReplaceLineEndings(" ").Trim();
        return body.Length > 1000 ? body[..1000] + "..." : body;
    }

    // Only attach a bearer when we actually have one. A loopback / unauthenticated source (e.g. local HAPI) resolves
    // to an empty token; sending "Authorization: Bearer " with no value trips some servers, so omit it entirely.
    private static void SetBearer(HttpRequestMessage request, string? accessToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    private static string RequireBaseUrl(FhirSourceConfiguration source)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            throw new InvalidOperationException("FHIR source base URL is required for bulk export.");
        }

        return source.BaseUrl.TrimEnd('/');
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
