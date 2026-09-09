using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.Observability.Logging;
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
        var exportStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var statusUrl = await KickOffExportAsync(request, source, cancellationToken);

        IReadOnlyList<BulkExportFile> files;
        for (var attempt = 0; ; attempt++)
        {
            var pollResult = await PollOnceAsync(statusUrl, source, cancellationToken);
            if (pollResult.Status == BulkExportPollStatus.Completed)
            {
                files = pollResult.Files ?? [];
                break;
            }

            if (pollResult.Status == BulkExportPollStatus.Failed)
            {
                throw new InvalidOperationException(pollResult.ErrorMessage ?? "Bulk export status poll failed.");
            }

            if (attempt + 1 >= _options.MaxPollAttempts)
            {
                throw new TimeoutException(
                    $"Bulk export did not complete after {_options.MaxPollAttempts} status polls.");
            }

            await _delay(pollResult.RetryAfter ?? TimeSpan.FromSeconds(Math.Max(1, _options.DefaultPollIntervalSeconds)), cancellationToken);
        }

        var resources = await DownloadResultsAsync(files, source, cancellationToken);

        _logger.LogInformation(
            LogEvents.BulkExportCompleted,
            "Bulk export completed for {SourceName}: {ResourceCount} resource(s) across {FileCount} NDJSON file(s) "
            + "in {ElapsedMs}ms.",
            source.Name, resources.Count, files.Count,
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(exportStartedAt).TotalMilliseconds);

        return resources;
    }

    public async Task<string> KickOffExportAsync(
        FhirBulkExportRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var baseUrl = RequireBaseUrl(source);
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        return await KickOffAsync(baseUrl, request, accessToken, cancellationToken);
    }

    public async Task<BulkExportPollResult> PollOnceAsync(
        string statusUrl,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        SetBearer(httpRequest, accessToken);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new BulkExportPollResult(
                BulkExportPollStatus.Completed,
                Files: ParseManifest(body, "output"),
                ErrorFiles: ParseManifest(body, "error"));
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return new BulkExportPollResult(BulkExportPollStatus.InProgress, RetryAfter: ResolvePollDelay(response));
        }

        var errorBody = await SafeReadAsync(response, cancellationToken);
        return new BulkExportPollResult(
            BulkExportPollStatus.Failed,
            ErrorMessage: $"Bulk export status poll returned {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}");
    }

    public async Task<IReadOnlyList<ResourceEnvelope>> DownloadResultsAsync(
        IReadOnlyList<BulkExportFile> files,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var resources = new List<ResourceEnvelope>();
        foreach (var file in files)
        {
            resources.AddRange(await DownloadWithRetryAsync(file, source, cancellationToken));
        }

        return resources;
    }

    public async Task<IReadOnlyList<BulkExportPartialFailure>> DownloadPartialFailuresAsync(
        IReadOnlyList<BulkExportFile> errorFiles,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (errorFiles.Count == 0)
        {
            return [];
        }

        var failures = new List<BulkExportPartialFailure>();
        foreach (var file in errorFiles)
        {
            // Re-acquire per file — same reason as DownloadResultsAsync: one token held across a long multi-file
            // download expires mid-loop and later files 401. The provider re-mints only when near expiry.
            var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
            failures.AddRange(await DownloadOperationOutcomesAsync(file, accessToken, cancellationToken));
        }

        return failures;
    }

    private async Task<IReadOnlyList<BulkExportPartialFailure>> DownloadOperationOutcomesAsync(
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
            // An error-file itself failing to download must not fail the whole (otherwise successful) export —
            // log and move on rather than throw, since the primary output data is unaffected.
            _logger.LogWarning(
                "Bulk export partial-failure file download returned {StatusCode} for {Url}; skipping.",
                (int)response.StatusCode, file.Url);
            return [];
        }

        var failures = new List<BulkExportPartialFailure>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            failures.AddRange(ParseOperationOutcome(line));
        }

        return failures;
    }

    /// <summary>Extracts every <c>issue</c> entry from one OperationOutcome NDJSON line. The FHIR Bulk Data spec's
    /// manifest <c>error</c> array has no structured field for which resource type an issue is about — Epic (and
    /// most servers) name it in <c>diagnostics</c> free text instead, so that's surfaced as-is rather than parsed
    /// further.</summary>
    private static IReadOnlyList<BulkExportPartialFailure> ParseOperationOutcome(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("issue", out var issues) ||
            issues.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var failures = new List<BulkExportPartialFailure>();
        foreach (var issue in issues.EnumerateArray())
        {
            var diagnostics = GetString(issue, "diagnostics");
            if (string.IsNullOrWhiteSpace(diagnostics))
            {
                diagnostics = issue.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
                    ? GetString(details, "text")
                    : null;
            }

            if (string.IsNullOrWhiteSpace(diagnostics))
            {
                continue;
            }

            failures.Add(new BulkExportPartialFailure(GetString(issue, "severity"), GetString(issue, "code"), diagnostics));
        }

        return failures;
    }

    public async Task CancelExportAsync(
        string statusUrl,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, statusUrl);
        SetBearer(httpRequest, accessToken);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        // A 404 means the export is already gone (cancelled/deleted/expired) — the caller's desired end state
        // already holds, so this is treated as success rather than an error, matching DELETE's idempotent semantics.
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Bulk export cancelled at {StatusUrl} ({StatusCode}).", statusUrl, (int)response.StatusCode);
            return;
        }

        var body = await SafeReadAsync(response, cancellationToken);
        throw new InvalidOperationException(
            $"Bulk export cancellation returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {statusUrl}. {body}");
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

        // A duplicate kick-off is not always a 4xx: some servers (e.g. eCW) reject one with HTTP 200 carrying an
        // OperationOutcome (issue code "duplicate" / text "already in progress"), so checking the status alone would
        // read a job that was never started as success. Inspect the payload — a single export runs per group at a
        // time, and the remedy (wait for or cancel the in-flight job) differs from a plain rejection.
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.TooManyRequests)
        {
            var body = await SafeReadAsync(response, cancellationToken);
            if (IsDuplicateJobResponse(body))
            {
                throw new InvalidOperationException(
                    $"Bulk export kick-off rejected as a duplicate for {kickOffUrl}: a bulk export is already " +
                    "running for this group. Wait for it to finish or cancel it before starting another.");
            }
        }

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            var body = await SafeReadAsync(response, cancellationToken);
            // Keep only a short snippet of the response body — some servers (e.g. Epic) return a full HTML error
            // page, which is noise in logs and must never reach the UI. The status + reason are what matter.
            var snippet = string.IsNullOrWhiteSpace(body)
                ? string.Empty
                : $" {(body.Length > 300 ? body[..300] + "…" : body)}";
            throw new InvalidOperationException(
                $"Bulk export kick-off returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {kickOffUrl}.{snippet}");
        }

        var statusUrl = response.Headers.Location?.ToString()
            ?? (response.Content.Headers.ContentLocation?.ToString());
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            throw new InvalidOperationException("Bulk export kick-off did not return a Content-Location status URL.");
        }

        // StatusUrl is the job's identity for its whole life — polling, completion, and the BulkExportJob row that
        // survives a Worker restart — so it is the join key between this submit and the completion event.
        _logger.LogInformation(
            LogEvents.BulkExportSubmitted,
            "Bulk export submitted against {BaseUrl}: Scope={ExportScope} GroupId={GroupId} "
            + "ResourceTypes=[{ResourceTypes}] Since={Since} OutputFormat={OutputFormat}. Polling status at {StatusUrl}.",
            baseUrl, request.Scope, request.GroupId,
            request.ResourceTypes is { Count: > 0 } types ? string.Join(", ", types) : "all",
            request.Since, request.OutputFormat, statusUrl);

        return statusUrl;
    }

    // Downloads one file, retrying transient failures on THAT file alone (each attempt with a freshly acquired token)
    // rather than letting a single flaky 401 / gateway error / timeout throw and restart the whole multi-file
    // download from scratch — which, against an intermittently-failing edge (eCW), can loop indefinitely on a small
    // export. The token is fetched per attempt so an expired/near-expired one is re-minted; the provider serves the
    // cached token while valid, so this stays cheap. Cancellation is never retried.
    private async Task<IReadOnlyList<ResourceEnvelope>> DownloadWithRetryAsync(
        BulkExportFile file,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, _options.MaxDownloadAttempts);
        for (var attempt = 1; ; attempt++)
        {
            var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
            try
            {
                return await DownloadNdjsonAsync(file, accessToken, cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Bulk export file download attempt {Attempt}/{MaxAttempts} failed for {Url}; retrying with a fresh token. {Error}",
                    attempt, maxAttempts, file.Url, ex.Message);
                var delaySeconds = Math.Max(1, _options.DownloadRetryDelaySeconds) * attempt;
                await _delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
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

    private static IReadOnlyList<BulkExportFile> ParseManifest(string body, string arrayPropertyName)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(arrayPropertyName, out var output) ||
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

    // Mirrors the SMART Bulk Data duplicate signal a status code cannot express: an OperationOutcome whose issue
    // carries code "duplicate", or free text saying an export is "already in progress". Kept resilient to a
    // truncated/whitespace-normalised body (the substring check) as well as a well-formed one (the JSON check).
    private static bool IsDuplicateJobResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (body.Contains("already in progress", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("issue", out var issues) &&
                issues.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in issues.EnumerateArray())
                {
                    if (string.Equals(GetString(issue, "code"), "duplicate", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON or truncated body — the substring check above is the fallback.
        }

        return false;
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
