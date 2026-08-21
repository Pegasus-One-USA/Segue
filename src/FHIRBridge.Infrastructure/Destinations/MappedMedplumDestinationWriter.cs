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
/// so re-running a pipeline never duplicates. When a record carries no usable identifier a synthetic one is stamped
/// from the source id and conditional-updated by it. Types that have <b>no</b> <c>identifier</c> search parameter in
/// FHIR R4 (Provenance, Binary, AuditEvent, …) can't be conditional-updated at all, so they target a deterministic v5
/// logical id derived from the source key — <c>PUT {base}/{ResourceType}/{uuid}</c> — and, because hosted Medplum will
/// not update-as-create a client-assigned id (a PUT to a non-existent id 404s), fall back to <c>POST {base}/{ResourceType}</c>
/// to create. (Trade-off: on such a server these types are not idempotent — re-runs create duplicates, as they carry no
/// business key to dedupe on.) Auth is OAuth2 <c>client_credentials</c> via
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

    // FHIR R4 resource types that define NO `identifier` element or search parameter, so an identifier-based
    // conditional update is impossible — Medplum rejects `PUT {type}?identifier=…` with 400 "Unknown search
    // parameter: identifier". These are upserted by a deterministic logical id instead (see BuildUpsert).
    private static readonly HashSet<string> IdentifierlessResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Provenance", "AuditEvent", "Binary", "Bundle", "Parameters"
    };

    // Fixed namespace for deriving stable v5 (name-based) UUIDs for identifier-less resources. Value is arbitrary but
    // MUST stay constant — changing it re-maps every source id to a new logical id and would duplicate on re-run.
    private static readonly Guid DeterministicIdNamespace = new("8b2f0b3e-3f4a-4c1d-9a7e-2c6d5f9b0a11");

    /// <summary>
    /// The write plan for one record: a primary conditional/logical-id <c>PUT</c>, plus an optional
    /// <c>POST</c>-create fallback. <see cref="CreatePath"/>/<see cref="CreateBody"/> are set only for identifier-less
    /// types — used when the primary PUT returns 404 because the server won't update-as-create a client-assigned id.
    /// </summary>
    private readonly record struct MedplumUpsert(
        string ResourceType, string PutPath, string PutBody, string? CreatePath, string? CreateBody);

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
        var metadata = MedplumConnectionMetadata.Parse(destination.ConnectionMetadataJson);

        // The FHIR base URL comes from Target on the synchronous write path; on the workflow-graph / bulk-export-resume
        // path the destination is reconstructed from node config and Target can be empty, so fall back to the base URL
        // carried in connection metadata.
        var baseUrl = (string.IsNullOrWhiteSpace(destination.Target) ? metadata.BaseUrl : destination.Target)?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Medplum destination requires a FHIR base URL (in Target or connection metadata, e.g. https://api.medplum.com/fhir/R4).");
        }

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

        if (metadata.UsesAsyncBatch)
        {
            try
            {
                return await WriteAsyncBatchAsync(httpClient, baseUrl!, tokenUrl, credential, metadata, records, cancellationToken);
            }
            catch (MedplumAsyncBatchUnavailableException ex)
            {
                // Medplum gates the respond-async batch feature per project (hosted api.medplum.com returns 400
                // "Async Batch feature not available"). Rather than fail the whole run, degrade gracefully to the
                // per-record conditional-PUT path — which is always available — so the async_batch preference never
                // silently exports nothing. Idempotent: no records were written by the aborted batch attempt (the
                // feature gate rejects the very first chunk's submit), and per-record upserts by identifier are safe
                // to (re)apply regardless.
                _logger.LogWarning(ex,
                    "Medplum async batch unavailable on this project; falling back to per-record writes for {Count} record(s).",
                    records.Count);
                return await WritePerRecordAsync(httpClient, baseUrl!, tokenUrl, credential, metadata, records, cancellationToken);
            }
        }

        return await WritePerRecordAsync(httpClient, baseUrl!, tokenUrl, credential, metadata, records, cancellationToken);
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

                var upsert = BuildUpsert(record, metadata.IdentifierSystem);
                var (status, errorBody) = await SendWithRateLimitRetryAsync(
                    httpClient, accessToken, HttpMethod.Put, $"{baseUrl}/{upsert.PutPath}", upsert.PutBody, cancellationToken);

                if (!IsSuccess(status))
                {
                    // Create-on-PUT fallback: hosted Medplum returns 404 on a PUT to a non-existent client-assigned
                    // id (its only create-if-missing is search-based conditional upsert, which identifier-less types
                    // like Provenance/Binary can't use). POST {type} to create instead. NOTE: on such a server this
                    // makes re-runs create duplicates for these types — there is no business key to dedupe on; types
                    // WITH an identifier are unaffected (they carry no CreatePath).
                    if (status == HttpStatusCode.NotFound && upsert.CreatePath is not null)
                    {
                        var (createStatus, createErrorBody) = await SendWithRateLimitRetryAsync(
                            httpClient, accessToken, HttpMethod.Post, $"{baseUrl}/{upsert.CreatePath}", upsert.CreateBody!, cancellationToken);
                        if (!IsSuccess(createStatus))
                        {
                            throw new HttpRequestException(
                                $"Medplum POST '{baseUrl}/{upsert.CreatePath}' returned {(int)createStatus}: {Truncate(createErrorBody)}");
                        }
                    }
                    else
                    {
                        throw new HttpRequestException(
                            $"Medplum PUT '{baseUrl}/{upsert.PutPath}' returned {(int)status}: {Truncate(errorBody)}");
                    }
                }

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
                    // Async-batch entries use the logical-id/conditional PUT only. Identifier-less types (Provenance,
                    // Binary) whose server won't update-as-create surface as per-entry 4xx and are isolated by
                    // TallyBatchResponse — the POST-create fallback lives on the per-record path (WritePerRecordAsync).
                    var upsert = BuildUpsert(record, metadata.IdentifierSystem);
                    entries.Add(new JsonObject
                    {
                        ["resource"] = JsonNode.Parse(upsert.PutBody),
                        ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = upsert.PutPath }
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
            catch (MedplumAsyncBatchUnavailableException)
            {
                // A project-wide feature gate, not a per-chunk failure: don't isolate it into recordErrors (which
                // would just fail every chunk the same way). Propagate so WriteAsync falls back to per-record.
                throw;
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
            // Surface Medplum's OperationOutcome on a non-2xx submit (e.g. a 400 rejecting the batch/async request)
            // instead of a bare "400 (Bad Request)" — mirrors the per-record path's error reporting.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Medplum gates respond-async batch per project; hosted api.medplum.com returns 400 with
                // details.text "Async Batch feature not available". Signal that distinctly so WriteAsync can fall
                // back to per-record instead of failing — any other non-2xx stays a hard error with its body.
                if (response.StatusCode == HttpStatusCode.BadRequest
                    && body.Contains("Async Batch feature not available", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MedplumAsyncBatchUnavailableException(
                        $"Medplum async batch not available on this project: {Truncate(body)}");
                }

                throw new HttpRequestException(
                    $"Medplum batch submit to '{baseUrl}' returned {(int)response.StatusCode}: {Truncate(body)}");
            }

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
    /// Produces the (resourceType, upsert URL path, body) for one record, choosing an idempotency strategy by type:
    /// <list type="number">
    ///   <item>Types with no <c>identifier</c> search parameter (<see cref="IdentifierlessResourceTypes"/>, e.g.
    ///   Provenance, Binary) can't be conditional-updated by identifier — Medplum 400s "Unknown search parameter:
    ///   identifier". They target a <b>deterministic v5 logical id</b> derived from the source system + type + id
    ///   (<c>PUT {type}/{uuid}</c>), idempotent on servers that update-as-create; when the server won't (hosted
    ///   Medplum 404s a client-assigned id), a <c>POST {type}</c> create fallback is supplied.</item>
    ///   <item>Otherwise a conditional update keyed on a business identifier (idempotent, no id remapping).</item>
    ///   <item>Otherwise a synthetic identifier stamped from the source id, then conditional-updated by it.</item>
    /// </list>
    /// </summary>
    private static MedplumUpsert BuildUpsert(
        MappedDestinationRecord record, string? preferredSystem)
    {
        if (string.IsNullOrWhiteSpace(record.SourceJson)
            || JsonNode.Parse(record.SourceJson) is not JsonObject resource
            || resource["resourceType"]?.GetValue<string>() is not { Length: > 0 } resourceType)
        {
            throw new InvalidOperationException(
                $"Medplum destination needs FHIR JSON (SourceJson) for '{record.ResourceType}'; none was present.");
        }

        if (IdentifierlessResourceTypes.Contains(resourceType))
        {
            // No identifier search parameter exists for this type, so conditional-update-by-identifier can't work.
            // Derive a stable logical id from the source key (system + type + source id) and PUT {type}/{uuid}. On a
            // server that update-as-creates a client-assigned id this is idempotent (re-runs update in place). Hosted
            // Medplum does NOT — a PUT to a non-existent id returns 404 — so we also hand back a POST {type} create
            // fallback (see WritePerRecordAsync). FHIR update semantics require the PUT body id to match the URL id;
            // the POST create drops it so the server assigns its own.
            var keySystem = string.IsNullOrWhiteSpace(preferredSystem) ? "urn:fhirbridge:source-id" : preferredSystem!;
            var keyValue = string.IsNullOrWhiteSpace(record.SourceResourceId)
                ? resource["id"]?.GetValue<string>()
                : record.SourceResourceId;
            var logicalId = string.IsNullOrWhiteSpace(keyValue)
                ? Guid.NewGuid().ToString() // no stable source key: fall back to a fresh id (not idempotent, but valid)
                : CreateNameBasedUuid(DeterministicIdNamespace, $"{keySystem}|{resourceType}|{keyValue}").ToString();

            resource["id"] = logicalId;
            var putBody = resource.ToJsonString(JsonOptions);
            resource.Remove("id"); // POST create: server owns id assignment.
            var createBody = resource.ToJsonString(JsonOptions);
            return new MedplumUpsert(resourceType, $"{resourceType}/{logicalId}", putBody, resourceType, createBody);
        }

        if (TrySelectIdentifier(resource, preferredSystem, out var system, out var value))
        {
            // Conditional update: PUT {type}?identifier={system}|{value}. Body id is dropped so the server assigns/
            // reuses its own logical id for the single match (or creates when there is none).
            resource.Remove("id");
            var query = string.IsNullOrEmpty(system)
                ? Uri.EscapeDataString(value)
                : $"{Uri.EscapeDataString(system)}%7C{Uri.EscapeDataString(value)}"; // %7C = '|'
            return new MedplumUpsert(resourceType, $"{resourceType}?identifier={query}", resource.ToJsonString(JsonOptions), null, null);
        }

        // Fallback: the resource carries no business identifier. A logical-id PUT ({type}/{sourceId}) does NOT work
        // for Medplum — it owns logical-id assignment (UUIDs) and rejects a client-chosen id with 400 "Invalid id"
        // (see Medplum migration guidance: preserve source keys as IDENTIFIERS, never as logical ids). So stamp a
        // synthetic identifier from the source id and conditional-upsert by it — idempotent (re-runs match the same
        // resource) and Medplum assigns its own logical id. Uses the configured identifier system when set, else a
        // stable urn marker so the value can't collide with a real coding system.
        var syntheticSystem = string.IsNullOrWhiteSpace(preferredSystem)
            ? "urn:fhirbridge:source-id"
            : preferredSystem!;
        var syntheticValue = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? resource["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N")
            : record.SourceResourceId!;

        resource.Remove("id");
        if (resource["identifier"] is not JsonArray identifiers)
        {
            identifiers = [];
            resource["identifier"] = identifiers;
        }

        identifiers.Add(new JsonObject { ["system"] = syntheticSystem, ["value"] = syntheticValue });

        var syntheticQuery = $"{Uri.EscapeDataString(syntheticSystem)}%7C{Uri.EscapeDataString(syntheticValue)}"; // %7C = '|'
        return new MedplumUpsert(resourceType, $"{resourceType}?identifier={syntheticQuery}", resource.ToJsonString(JsonOptions), null, null);
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

    /// <summary>
    /// Builds an RFC 4122 §4.3 name-based (version 5, SHA-1) UUID from a namespace and name. Deterministic: the same
    /// inputs always produce the same GUID, which is what makes identifier-less resources idempotent across re-runs.
    /// </summary>
    private static Guid CreateNameBasedUuid(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapGuidByteOrder(namespaceBytes); // .NET stores the first three fields little-endian; RFC hashes big-endian.

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var toHash = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, toHash, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, toHash, namespaceBytes.Length, nameBytes.Length);

        var hash = System.Security.Cryptography.SHA1.HashData(toHash);

        var uuid = new byte[16];
        Array.Copy(hash, 0, uuid, 0, 16);
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapGuidByteOrder(uuid); // back to .NET little-endian field order
        return new Guid(uuid);
    }

    private static void SwapGuidByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }

    /// <summary>
    /// Sends one write, retrying on HTTP 429 (rate limited). Returns the terminal status and — on a non-2xx — the
    /// response body (Medplum's OperationOutcome diagnostics, not PHI), so the caller can decide success vs. a
    /// create-on-PUT fallback vs. a hard error. Only a 429 that exhausts retries, or a network failure, throws.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> SendWithRateLimitRetryAsync(
        HttpClient httpClient, string accessToken, HttpMethod method, string url, string body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                var errorBody = response.IsSuccessStatusCode
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(cancellationToken);
                return (response.StatusCode, errorBody);
            }

            if (attempt >= MaxRateLimitRetries)
            {
                throw new HttpRequestException(
                    $"Medplum returned 429 (rate limited) after {MaxRateLimitRetries} attempts for '{method} {url}'.");
            }

            var delay = GetRetryDelay(response);
            _logger.LogInformation(
                "Medplum rate limited (429); retry {Attempt}/{Max} after {DelaySeconds}s",
                attempt, MaxRateLimitRetries, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

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

    // Caps a server error body so one rejected write's diagnostics can't flood the log.
    private static string Truncate(string value) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= 600 ? value : value[..600] + "…";
}

/// <summary>
/// Signals that the Medplum project does not have the respond-async batch feature enabled (hosted api.medplum.com
/// returns HTTP 400 with details.text "Async Batch feature not available"). Distinct from a genuine request error so
/// <see cref="MappedMedplumDestinationWriter"/> can fall back from <c>async_batch</c> to the always-available
/// per-record write path instead of failing the whole run.
/// </summary>
internal sealed class MedplumAsyncBatchUnavailableException : Exception
{
    public MedplumAsyncBatchUnavailableException(string message) : base(message)
    {
    }
}
