using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes resources into a Medplum FHIR R4 store. Medplum is FHIR-native, so each mapped record's normalized source
/// FHIR JSON (<see cref="MappedDestinationRecord.SourceJson"/>) is persisted as-is. Writes are made <b>idempotent</b>
/// via conditional update — <c>PUT {base}/{ResourceType}?identifier={system}|{value}</c> — keyed on the resource's
/// business identifier (see <see cref="MedplumConnectionMetadata.IdentifierSystem"/>): 0 matches creates, 1 updates,
/// so re-running a pipeline never duplicates. When a record has no usable identifier it falls back to a logical-id
/// <c>PUT {base}/{ResourceType}/{id}</c> (update-or-create). Auth is OAuth2 <c>client_credentials</c> via
/// <see cref="IMedplumTokenProvider"/> (client_secret or private_key_jwt); the writer is rate-limit aware and backs
/// off on HTTP 429.
///
/// Two write strategies, chosen by <see cref="MedplumConnectionMetadata.WriteMode"/>:
/// <list type="bullet">
///   <item><b>per_record</b> (default) — one conditional PUT per record. Simple; best for small runs.</item>
///   <item><b>async_batch</b> — chunk records into <c>Prefer: respond-async</c> batch Bundles of the same conditional
///   PUTs, poll the job to completion, and tally per-entry results. Async batch work is exempt from Medplum's weighted
///   interaction quota, so this is the throughput path for bulk loads (see docs/backend/15-medplum-integration-plan.md §5).</item>
/// </list>
/// </summary>
public sealed class MappedMedplumDestinationWriter : IConfiguredDestinationWriter
{
    private const int MaxRateLimitRetries = 5;
    private const int MaxAsyncPollAttempts = 60;
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMedplumTokenProvider _tokenProvider;
    private readonly ILogger<MappedMedplumDestinationWriter> _logger;

    public MappedMedplumDestinationWriter(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IMedplumTokenProvider tokenProvider,
        ILogger<MappedMedplumDestinationWriter> logger)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var baseUrl = destination.Target?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Medplum destination requires a FHIR base URL in Target (e.g. https://api.medplum.com/fhir/R4).");
        }

        var metadata = MedplumConnectionMetadata.Parse(destination.ConnectionMetadataJson);
        if (string.IsNullOrWhiteSpace(metadata.ClientId))
        {
            throw new InvalidOperationException(
                "Medplum destination requires 'medplumClientId' in connection metadata.");
        }

        // The SecretReference holds the client secret for the client_secret flow, or the PEM private key for the
        // private_key_jwt (SMART Backend Services) flow — the auth method chosen in connection metadata decides which.
        var secretMaterial = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        MedplumClientCredential credential = metadata.UsesPrivateKeyJwt
            ? new MedplumPrivateKeyJwtCredential(metadata.ClientId!, secretMaterial, metadata.KeyId)
            : new MedplumClientSecretCredential(metadata.ClientId!, secretMaterial);

        var tokenUrl = metadata.ResolveTokenUrl(baseUrl!);
        var httpClient = _httpClientFactory.CreateClient(nameof(MappedMedplumDestinationWriter));

        return metadata.UsesAsyncBatch
            ? await WriteAsyncBatchAsync(httpClient, baseUrl!, tokenUrl, credential, metadata, records, cancellationToken)
            : await WritePerRecordAsync(httpClient, baseUrl!, tokenUrl, credential, metadata, records, cancellationToken);
    }

    private async Task<DestinationWriteResult> WritePerRecordAsync(
        HttpClient httpClient,
        string baseUrl,
        string tokenUrl,
        MedplumClientCredential credential,
        MedplumConnectionMetadata metadata,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        var written = 0;
        var recordErrors = new List<string>();
        var writtenIds = new List<string?>();

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var accessToken = await _tokenProvider.GetAccessTokenAsync(
                    tokenUrl, credential, cancellationToken);

                var (_, upsertPath, body) = BuildUpsert(record, metadata.IdentifierSystem);
                await SendWithRateLimitRetryAsync(
                    httpClient, accessToken, $"{baseUrl}/{upsertPath}", body, cancellationToken);

                written++;
                writtenIds.Add(record.SourceResourceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Isolate the failing record so one bad resource doesn't sink the whole batch — same contract the
                // relational writers use. The message is intentionally free of resource payload (no PHI).
                _logger.LogWarning(ex,
                    "Medplum write failed for {ResourceType} record {SourceResourceId}",
                    record.ResourceType, record.SourceResourceId);
                recordErrors.Add($"{record.ResourceType}/{record.SourceResourceId}: {ex.Message}");
            }
        }

        return new DestinationWriteResult(
            written,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenIds);
    }

    /// <summary>
    /// Writes records as async batch Bundles of conditional PUTs. Records are chunked
    /// (<see cref="MedplumConnectionMetadata.EffectiveBatchSize"/>); each chunk is POSTed to the FHIR base with
    /// <c>Prefer: respond-async</c>, the returned job is polled to completion, and the batch-response Bundle's
    /// per-entry statuses are tallied back to records (bundle responses preserve request order). A record with no FHIR
    /// JSON, or an entry whose response is non-2xx, is isolated into <see cref="DestinationWriteResult.RecordErrors"/>.
    /// </summary>
    private async Task<DestinationWriteResult> WriteAsyncBatchAsync(
        HttpClient httpClient,
        string baseUrl,
        string tokenUrl,
        MedplumClientCredential credential,
        MedplumConnectionMetadata metadata,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        var written = 0;
        var recordErrors = new List<string>();
        var writtenIds = new List<string?>();

        foreach (var chunk in records.Chunk(metadata.EffectiveBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Build one entry per record that has usable FHIR JSON; records that don't are isolated up front.
            var entries = new JsonArray();
            var entryRecords = new List<MappedDestinationRecord>();
            foreach (var record in chunk)
            {
                try
                {
                    var (_, upsertPath, body) = BuildUpsert(record, metadata.IdentifierSystem);
                    entries.Add(new JsonObject
                    {
                        ["resource"] = JsonNode.Parse(body),
                        ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = upsertPath }
                    });
                    entryRecords.Add(record);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    recordErrors.Add($"{record.ResourceType}/{record.SourceResourceId}: {ex.Message}");
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            var bundle = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "batch",
                ["entry"] = entries
            }.ToJsonString(JsonOptions);

            try
            {
                var accessToken = await _tokenProvider.GetAccessTokenAsync(tokenUrl, credential, cancellationToken);
                var responseBundle = await SubmitAsyncBatchAsync(httpClient, accessToken, baseUrl, bundle, cancellationToken);
                TallyBatchResponse(responseBundle, entryRecords, ref written, recordErrors, writtenIds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A whole-chunk failure (submit/poll error) isolates every record in the chunk rather than aborting.
                _logger.LogWarning(ex, "Medplum async batch chunk of {Count} record(s) failed", entryRecords.Count);
                foreach (var record in entryRecords)
                {
                    recordErrors.Add($"{record.ResourceType}/{record.SourceResourceId}: {ex.Message}");
                }
            }
        }

        return new DestinationWriteResult(
            written,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenIds);
    }

    /// <summary>
    /// POSTs a batch Bundle with <c>Prefer: respond-async</c> and returns the batch-response Bundle. Handles the
    /// three shapes a server may return: a synchronous 200 with the Bundle inline; a 202 + <c>Content-Location</c>
    /// job URL that is polled until 200; and a completed job whose payload is an <c>AsyncJob</c> pointing at a
    /// <c>Binary</c> that must be fetched for the Bundle. Rate-limit (429) aware on both the POST and the polls.
    /// </summary>
    private async Task<JsonObject?> SubmitAsyncBatchAsync(
        HttpClient httpClient, string accessToken, string baseUrl, string bundleJson, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
            {
                Content = new StringContent(bundleJson, Encoding.UTF8, "application/fhir+json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Prefer", "respond-async");

            response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                break;
            }

            response.Dispose();
            if (attempt >= MaxRateLimitRetries)
            {
                throw new HttpRequestException(
                    $"Medplum returned 429 (rate limited) after {MaxRateLimitRetries} attempts submitting a batch bundle.");
            }

            await Task.Delay(GetRetryDelay(response), cancellationToken);
        }

        using (response)
        {
            // Server honored the async preference: poll the job to completion.
            if (response.StatusCode == HttpStatusCode.Accepted
                && response.Headers.Location is { } jobUrl)
            {
                return await PollAsyncJobAsync(httpClient, accessToken, jobUrl.ToString(), cancellationToken);
            }

            // Server responded synchronously (respond-async is only a preference): the body is the batch response.
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return await ResolveResponseBundleAsync(httpClient, accessToken, body, cancellationToken);
        }
    }

    private async Task<JsonObject?> PollAsyncJobAsync(
        HttpClient httpClient, string accessToken, string jobUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAsyncPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, jobUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                await Task.Delay(GetPollDelay(response), cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(GetRetryDelay(response), cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return await ResolveResponseBundleAsync(httpClient, accessToken, body, cancellationToken);
        }

        throw new TimeoutException(
            $"Medplum async batch job did not complete after {MaxAsyncPollAttempts} polls ('{jobUrl}').");
    }

    /// <summary>
    /// Coerces a completed-job payload into the batch-response Bundle. Returns it directly when it already is a
    /// Bundle; when it is an <c>AsyncJob</c> whose <c>output</c> references a Binary URL, fetches and parses that.
    /// </summary>
    private async Task<JsonObject?> ResolveResponseBundleAsync(
        HttpClient httpClient, string accessToken, string body, CancellationToken cancellationToken)
    {
        if (JsonNode.Parse(body) is not JsonObject root)
        {
            return null;
        }

        if (string.Equals(root["resourceType"]?.GetValue<string>(), "Bundle", StringComparison.Ordinal))
        {
            return root;
        }

        // AsyncJob completion: output[].url points at a Binary holding the batch-response Bundle.
        if (root["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                if ((item as JsonObject)?["url"]?.GetValue<string>() is { Length: > 0 } binaryUrl)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, binaryUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    using var response = await httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var binaryBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    return JsonNode.Parse(binaryBody) as JsonObject;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a batch-response Bundle's per-entry statuses back onto the submitted records (bundle responses preserve
    /// request order). 2xx counts as written; anything else is isolated into <paramref name="recordErrors"/>. When the
    /// response can't be parsed into entries, the whole chunk is conservatively treated as succeeded (it did complete
    /// without an HTTP error), with a warning so silent under-reporting is visible.
    /// </summary>
    private void TallyBatchResponse(
        JsonObject? responseBundle,
        IReadOnlyList<MappedDestinationRecord> entryRecords,
        ref int written,
        List<string> recordErrors,
        List<string?> writtenIds)
    {
        if (responseBundle?["entry"] is not JsonArray responseEntries)
        {
            _logger.LogWarning(
                "Medplum async batch completed but returned no parseable response entries; counting all {Count} as written",
                entryRecords.Count);
            written += entryRecords.Count;
            foreach (var record in entryRecords)
            {
                writtenIds.Add(record.SourceResourceId);
            }

            return;
        }

        for (var i = 0; i < entryRecords.Count; i++)
        {
            var record = entryRecords[i];
            var status = i < responseEntries.Count
                ? (responseEntries[i] as JsonObject)?["response"]?["status"]?.GetValue<string>()
                : null;

            if (IsSuccessStatus(status))
            {
                written++;
                writtenIds.Add(record.SourceResourceId);
            }
            else
            {
                recordErrors.Add($"{record.ResourceType}/{record.SourceResourceId}: batch entry status '{status ?? "(missing)"}'");
            }
        }
    }

    // FHIR bundle entry response.status is like "200 OK" / "201 Created" / "412 Precondition Failed".
    private static bool IsSuccessStatus(string? status) =>
        !string.IsNullOrEmpty(status)
        && int.TryParse(status.AsSpan(0, Math.Min(3, status.Length)), out var code)
        && code is >= 200 and < 300;

    private static TimeSpan GetPollDelay(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? delta : DefaultPollInterval;

    /// <summary>
    /// Produces the (resourceType, upsert URL path, body) for one record. Prefers a conditional update keyed on a
    /// business identifier (idempotent, no id remapping); falls back to logical-id update-or-create when the resource
    /// carries no usable identifier.
    /// </summary>
    private static (string ResourceType, string UpsertPath, string Body) BuildUpsert(
        MappedDestinationRecord record, string? preferredSystem)
    {
        if (string.IsNullOrWhiteSpace(record.SourceJson)
            || JsonNode.Parse(record.SourceJson) is not JsonObject resource
            || resource["resourceType"]?.GetValue<string>() is not { Length: > 0 } resourceType)
        {
            throw new InvalidOperationException(
                $"Medplum destination needs FHIR JSON (SourceJson) for '{record.ResourceType}'; none was present.");
        }

        if (TrySelectIdentifier(resource, preferredSystem, out var system, out var value))
        {
            // Conditional update: PUT {type}?identifier={system}|{value}. Body id is dropped so the server assigns/
            // reuses its own logical id for the single match (or creates when there is none).
            resource.Remove("id");
            var query = string.IsNullOrEmpty(system)
                ? Uri.EscapeDataString(value)
                : $"{Uri.EscapeDataString(system)}%7C{Uri.EscapeDataString(value)}"; // %7C = '|'
            return (resourceType, $"{resourceType}?identifier={query}", resource.ToJsonString(JsonOptions));
        }

        // Fallback: logical-id update-or-create. Reconcile the body id to the URL so a validating server accepts it.
        var id = resource["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            id = string.IsNullOrWhiteSpace(record.SourceResourceId)
                ? Guid.NewGuid().ToString("N")
                : record.SourceResourceId;
            resource["id"] = id;
        }

        return (resourceType, $"{resourceType}/{Uri.EscapeDataString(id!)}", resource.ToJsonString(JsonOptions));
    }

    /// <summary>
    /// Picks the identifier to upsert on: the one whose <c>system</c> matches <paramref name="preferredSystem"/> when
    /// configured, else the first identifier that has a value. Returns false when the resource has none.
    /// </summary>
    private static bool TrySelectIdentifier(JsonObject resource, string? preferredSystem, out string system, out string value)
    {
        system = string.Empty;
        value = string.Empty;

        if (resource["identifier"] is not JsonArray identifiers || identifiers.Count == 0)
        {
            return false;
        }

        JsonObject? chosen = null;
        foreach (var node in identifiers)
        {
            if (node is not JsonObject id || string.IsNullOrWhiteSpace(id["value"]?.GetValue<string>()))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(preferredSystem)
                && string.Equals(id["system"]?.GetValue<string>(), preferredSystem, StringComparison.OrdinalIgnoreCase))
            {
                chosen = id;
                break;
            }

            chosen ??= id;
        }

        if (chosen is null)
        {
            return false;
        }

        system = chosen["system"]?.GetValue<string>() ?? string.Empty;
        value = chosen["value"]!.GetValue<string>();
        return true;
    }

    private async Task SendWithRateLimitRetryAsync(
        HttpClient httpClient, string accessToken, string url, string body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                response.EnsureSuccessStatusCode();
                return;
            }

            if (attempt >= MaxRateLimitRetries)
            {
                throw new HttpRequestException(
                    $"Medplum returned 429 (rate limited) after {MaxRateLimitRetries} attempts for '{url}'.");
            }

            var delay = GetRetryDelay(response);
            _logger.LogInformation(
                "Medplum rate limited (429); retry {Attempt}/{Max} after {DelaySeconds}s",
                attempt, MaxRateLimitRetries, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>Honors Retry-After (delta-seconds), else the reset window in the <c>RateLimit</c> header, else a default.</summary>
    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        // RateLimit: "requests";r=0;t=42, "fhirInteractions";r=... — take the smallest positive t as the wait.
        if (response.Headers.TryGetValues("RateLimit", out var values))
        {
            foreach (var raw in values)
            {
                foreach (var segment in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var tIndex = segment.IndexOf("t=", StringComparison.OrdinalIgnoreCase);
                    if (tIndex >= 0 && int.TryParse(segment[(tIndex + 2)..].TrimEnd(';'), out var t) && t > 0)
                    {
                        return TimeSpan.FromSeconds(t);
                    }
                }
            }
        }

        return DefaultRetryAfter;
    }
}
