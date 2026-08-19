using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FHIRBridge.Infrastructure.Terminology;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes resources to a FHIR repository via REST <c>PUT [base]/{ResourceType}/{id}</c> (update-or-create).
/// When the mapped record carries the normalized source FHIR JSON (<see cref="MappedDestinationRecord.SourceJson"/>),
/// that valid FHIR resource is persisted as <c>application/fhir+json</c> — with its <c>id</c> reconciled to the URL so
/// the update contract holds on a validating server (e.g. HAPI, Aidbox). When no FHIR JSON is present (non-FHIR
/// flows), it falls back to posting the flattened mapped payload to a permissive ingestion endpoint.
///
/// Authentication is opt-in via the non-secret <c>dest_fhirAuthType</c> metadata flag (absent/"none", "bearer", or
/// "clientCredentials" — see <see cref="FhirRepositoryAuthResolver"/>). When absent or "none" — every
/// <c>FhirRepository</c> row before this capability existed — base-URL resolution and the write loop are byte-for-
/// byte identical to before: no secret re-parsing, no header attached.
///
/// Writing is opt-in via the non-secret <c>dest_fhirWriteMode</c> metadata flag (absent/"individual", "bundle", or
/// "transaction"). Absent/"individual" — every row before this capability existed — keeps the exact one-<c>PUT</c>-
/// per-record loop, byte-for-byte. "bundle" and "transaction" both send every record with real FHIR JSON as one FHIR
/// <c>Bundle</c> POSTed once to the repository root, differing only in the Bundle's own <c>type</c> and — as a direct
/// consequence — their failure-isolation model:
/// <list type="bullet">
/// <item>"bundle" sends <c>type: "batch"</c>, which the FHIR spec defines as non-atomic — each entry is validated
/// independently, so the response Bundle's own per-entry <c>response.status</c>/<c>outcome</c> isolates one record's
/// failure from the rest without needing separate per-record try/catch logic. Its blind spot: a batch entry is
/// validated against whatever the server has already committed SO FAR in this same batch — two records that
/// mutually reference each other (e.g. an Observation's <c>hasMember</c> pointing at a sibling that itself
/// <c>derivedFrom</c>-references it back) can never both be resolvable no matter what order they're sent in, since
/// each needs the other to exist first.</item>
/// <item>"transaction" sends <c>type: "transaction"</c> instead — atomic per the FHIR spec: the destination resolves
/// every entry's references against the full set being written, not just what's committed so far, so mutually-
/// referencing records in the SAME write resolve correctly with no reordering needed (confirmed live against a real
/// Aidbox instance — the exact scenario above). The cost is the isolation "bundle" mode gets for free: if the
/// destination can't commit the whole transaction, NONE of its entries are written — one write-level failure, not
/// per-record. See <see cref="WriteTransactionAsync"/>.</item>
/// </list>
/// Both are gated behind this flag (rather than made unconditional) because each changes the wire format and failure
/// semantics for every row that opts in — enable either per-destination, confirm it behaves as the spec describes for
/// that specific server, before ever considering either a new default. A record with no FHIR JSON (the non-FHIR
/// fallback flow) is structurally incompatible with a Bundle entry and always goes out as its own individual
/// request, in all three modes, unchanged.
///
/// Before either write path runs, every record's embedded FHIR references are resolved against three states: already
/// present in this same batch (no destination lookup needed); absent from the batch but already present at the
/// destination (checked via a batched existence lookup — no warning, no re-fetch, no re-write); or genuinely
/// unresolved (absent from both). A record with a genuinely unresolved reference is excluded from the write entirely
/// and reported with the precise Type/id that's missing, instead of being sent with a reference that's guaranteed to
/// dangle. See <see cref="ResolveMissingReferencesAsync"/>.
/// </summary>
public sealed class MappedFhirRepositoryDestinationWriter : IConfiguredDestinationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>Maximum candidate ids per existence-check request for one resource type — keeps the <c>_id=a,b,c</c>
    /// query string well under any server's URL-length limit. Same caution the bundle-mode doc comment already
    /// applies to POSTed Bundles: this destination's real search behavior hasn't been live-verified any more than
    /// its batch-response shape has, so this stays a conservative, easily-tuned constant rather than an assumption
    /// baked into the query-building code.</summary>
    private const int ExistenceCheckChunkSize = 50;

    /// <summary>Default cap on how many distinct confirmed-missing references <c>dest_autoFetchMissingReferences</c>
    /// will fetch from the source EHR in one write call — each is a separate round trip (no batching trick exists
    /// for a source-side read-by-id the way <see cref="ExistenceCheckChunkSize"/> batches the Aidbox side), so an
    /// unusually reference-heavy batch falls back to blocking the excess instead of issuing an unbounded number of
    /// source calls. Overridable per destination via <c>dest_autoFetchMaxCount</c>.</summary>
    private const int DefaultAutoFetchMaxCount = 25;

    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFhirDestinationTokenProvider _tokenProvider;

    public MappedFhirRepositoryDestinationWriter(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IFhirDestinationTokenProvider tokenProvider)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var authType = ConnectionMetadataReader.GetString(destination.ConnectionMetadataJson, "dest_fhirAuthType") ?? "none";

        string baseUrl;
        System.Net.Http.Headers.AuthenticationHeaderValue? authHeader;
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase))
        {
            // Exactly today's behavior — unchanged for every existing FhirRepository row.
            baseUrl = (destination.Target ?? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)).TrimEnd('/');
            authHeader = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(destination.Target))
            {
                throw new InvalidOperationException(
                    "FHIR repository destinations with dest_fhirAuthType other than 'none' must set Target to the FHIR " +
                    "base URL — the secret is reserved for auth credentials, not the URL.");
            }

            baseUrl = destination.Target.TrimEnd('/');
            authHeader = await FhirRepositoryAuthResolver.ResolveAsync(
                destination.ConnectionMetadataJson, destination.SecretReference, _secretProvider, _tokenProvider, cancellationToken);
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(MappedFhirRepositoryDestinationWriter));

        // Resolves each record's (resourceType, id)-reconciled FHIR JSON exactly once and reuses it everywhere below
        // (reference resolution, ordering input, and the eventual write) — a record's id, when neither the source
        // JSON nor MappedDestinationRecord.SourceResourceId supplies one, is a freshly generated GUID; resolving
        // more than once per record would risk two different calls minting two different GUIDs for the same
        // record, breaking the exact identity match this destination writes it under.
        var resolvedResourceCache = new Dictionary<MappedDestinationRecord, (string ResourceType, JsonObject Resource)?>(
            ReferenceEqualityComparer.Instance);

        var autoFetchEnabled = string.Equals(
            ConnectionMetadataReader.GetString(destination.ConnectionMetadataJson, "dest_autoFetchMissingReferences"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var autoFetchMaxCount = ConnectionMetadataReader.GetInt(
            destination.ConnectionMetadataJson, "dest_autoFetchMaxCount", DefaultAutoFetchMaxCount);

        var (writableRecords, referenceErrors) = await ResolveMissingReferencesAsync(
            httpClient, baseUrl, authHeader, records, resolvedResourceCache,
            autoFetchEnabled, autoFetchMaxCount, context.FetchMissingReferenceAsync, cancellationToken);

        records = OrderRecordsByFhirReferenceDependency(writableRecords, resolvedResourceCache);

        // Best-effort, pre-write warning for a gap this destination genuinely can't help with: a resource type
        // referenced by something in THIS batch but never included in it at all — this destination's own resource
        // selection (dest_resources) is the only thing that decides what's ever fetched now (manual selection is
        // fully in control again — see SourceNodeExecutors.GetDestinationResourceTypesAsync), so this fires
        // whenever a selected type references one the user didn't also check (e.g. keeping Encounter but excluding
        // Location) — surfacing a clear pre-write message instead of Aidbox's own opaque 422. Surfaced through the
        // same RecordErrors field per-record write failures already use, so it shows up alongside them rather
        // than needing new UI.
        var writeMode = ConnectionMetadataReader.GetString(destination.ConnectionMetadataJson, "dest_fhirWriteMode") ?? "individual";
        var isTransactionMode = string.Equals(writeMode, "transaction", StringComparison.OrdinalIgnoreCase);
        var isBundleFamily = isTransactionMode || string.Equals(writeMode, "bundle", StringComparison.OrdinalIgnoreCase);

        if (!isBundleFamily)
        {
            // Exactly today's behavior — byte-for-byte — when dest_fhirWriteMode is absent or anything other than
            // "bundle"/"transaction". One PUT per writable record; a failure throws and fails the whole route, as
            // before. Records with a confirmed-missing reference were already excluded from `records` above and
            // never reach here.
            foreach (var record in records)
            {
                var (resourceType, resourceId, body) = BuildFhirResource(record, resolvedResourceCache);
                var endpoint = $"{baseUrl}/{resourceType}/{resourceId}";
                using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
                };
                if (authHeader is not null)
                {
                    request.Headers.Authorization = authHeader;
                }

                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
            }

            return new DestinationWriteResult(
                records.Count,
                RecordErrors: referenceErrors.Count > 0 ? referenceErrors : null);
        }

        // Bundle mode: split into records with real FHIR JSON (bundleable) and the non-FHIR fallback flow
        // (structurally incompatible with a Bundle entry — always sent individually, same as today).
        var bundleEntries = new List<(MappedDestinationRecord Record, string ResourceType, JsonObject Resource)>();
        var fallbackRecords = new List<MappedDestinationRecord>();
        foreach (var record in records)
        {
            if (TryParseFhirResource(record, resolvedResourceCache, out var resourceType, out var resource))
            {
                bundleEntries.Add((record, resourceType, resource));
            }
            else
            {
                fallbackRecords.Add(record);
            }
        }

        var recordErrors = new List<string>(referenceErrors);
        var writtenResourceIds = new List<string?>();

        if (bundleEntries.Count > 0)
        {
            if (isTransactionMode)
            {
                await WriteTransactionAsync(httpClient, baseUrl, authHeader, bundleEntries, recordErrors, writtenResourceIds, cancellationToken);
            }
            else
            {
                await WriteBundleAsync(httpClient, baseUrl, authHeader, bundleEntries, recordErrors, writtenResourceIds, cancellationToken);
            }
        }

        foreach (var record in fallbackRecords)
        {
            var (resourceType, resourceId, body) = BuildFhirResource(record, resolvedResourceCache);
            var endpoint = $"{baseUrl}/{resourceType}/{resourceId}";
            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
            };
            if (authHeader is not null)
            {
                request.Headers.Authorization = authHeader;
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            writtenResourceIds.Add(record.SourceResourceId);
        }

        return new DestinationWriteResult(
            writtenResourceIds.Count,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenResourceIds.Count > 0 || recordErrors.Count > 0 ? writtenResourceIds : null);
    }

    /// <summary>
    /// Resolves every FHIR reference — recursively — against three states, in order: (1) present (either in the
    /// original batch, or in a record fetched earlier in this same resolution — see below) — no destination lookup
    /// needed; (2) absent, but already exists at the destination — confirmed via a batched existence lookup, still no
    /// warning and no re-write; (3) absent from both — genuinely unresolved. A record whose reference resolves to
    /// state 3 is excluded from the returned writable set and reported with a precise Type/id error, instead of being
    /// sent to the destination with a reference that's guaranteed to dangle. A lookup failure (timeout, 5xx, network
    /// error) is deliberately treated as a fourth, distinct "inconclusive" state — NOT as confirmed-missing: this
    /// destination's HttpClient has no retry/circuit-breaker underneath it, so a single transient failure is a
    /// routine occurrence here, and wrongly withholding a clinical write over a transient blip is worse than the
    /// pre-existing risk of a dangling reference (which Aidbox's own validation, if configured to enforce it, remains
    /// the final backstop for either way). Inconclusive references still write, with a distinct, clearly-worded
    /// warning instead of a block.
    ///
    /// <para><b>Recursive/opt-in auto-fetch (<c>dest_autoFetchMissingReferences</c>):</b> a reference confirmed
    /// missing from both the batch and the destination can be fetched directly from whatever source EHR fed this
    /// run. Critically, an auto-fetched resource is indexed exactly like a top-level record — its own embedded
    /// references get the identical present/existing/missing treatment, recursively, round by round, until a round
    /// finds nothing new to resolve. This closes a real gap: an auto-fetched Encounter (pulled in only because some
    /// Observation referenced it) can itself reference a Practitioner that was never selected/fetched either — that
    /// second-level reference must be checked too, or the fetched Encounter just carries a fresh dangling reference
    /// into the write. All rounds share one <c>dest_autoFetchMaxCount</c> budget (not reset per level), so total
    /// source round-trips stays bounded regardless of how deep a reference chain runs.</para>
    ///
    /// <para><b>Cascading exclusion:</b> if a reference is still unresolved after every round (fetch disabled, no
    /// delegate wired, fetch failed, or the budget ran out), every record that depends on it — directly OR
    /// transitively, including an auto-fetched record whose OWN reference never resolved — is excluded, via a
    /// fixed-point pass: excluding a record adds its own identity to the missing set, so anything that referenced
    /// THAT record is caught in the next pass. Without this, an excluded auto-fetched Encounter would still be
    /// pointed at by whatever record referenced it, reproducing the exact dangling-reference problem this exists to
    /// prevent, just one level removed.</para>
    /// </summary>
    private static async Task<(List<MappedDestinationRecord> WritableRecords, List<string> Errors)> ResolveMissingReferencesAsync(
        HttpClient httpClient,
        string baseUrl,
        System.Net.Http.Headers.AuthenticationHeaderValue? authHeader,
        IReadOnlyCollection<MappedDestinationRecord> records,
        Dictionary<MappedDestinationRecord, (string ResourceType, JsonObject Resource)?> resolvedResourceCache,
        bool autoFetchEnabled,
        int autoFetchMaxCount,
        Func<string, string, CancellationToken, Task<string?>>? fetchMissingReferenceAsync,
        CancellationToken cancellationToken)
    {
        var present = new HashSet<ResourceReference>();
        var identityByRecord = new Dictionary<MappedDestinationRecord, ResourceReference>(ReferenceEqualityComparer.Instance);
        var referencesByRecord = new Dictionary<MappedDestinationRecord, List<ResourceReference>>(ReferenceEqualityComparer.Instance);

        // Shared by every record this resolution ever sees — original or auto-fetched — so recursion needs no
        // separate code path: a fetched record is indexed exactly like a top-level one.
        void IndexRecord(MappedDestinationRecord record)
        {
            if (TryParseFhirResource(record, resolvedResourceCache, out var resourceType, out var resource))
            {
                var identity = new ResourceReference(resourceType, resource["id"]!.GetValue<string>());
                present.Add(identity);
                identityByRecord[record] = identity;
            }

            var referenced = ExtractReferencedResources(record.SourceJson);
            if (referenced.Count > 0)
            {
                referencesByRecord[record] = referenced;
            }
        }

        foreach (var record in records)
        {
            IndexRecord(record);
        }

        var confirmedMissing = new HashSet<ResourceReference>();
        var inconclusiveTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var everConsidered = new HashSet<ResourceReference>();
        var allFetchedRecords = new List<MappedDestinationRecord>();
        var pipelineRunId = records.FirstOrDefault()?.PipelineRunId ?? Guid.Empty;
        var autoFetchBudgetRemaining = autoFetchMaxCount;
        var skippedDueToCapCount = 0;
        var frontier = records.ToList();
        var fetchFailureReasons = new Dictionary<ResourceReference, string>();

        while (frontier.Count > 0)
        {
            var candidatesByType = frontier
                .SelectMany(r => referencesByRecord.TryGetValue(r, out var refs) ? refs : [])
                .Where(r => !present.Contains(r) && everConsidered.Add(r))
                .GroupBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g.Select(r => r.Id).Distinct(StringComparer.Ordinal).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            if (candidatesByType.Count == 0)
            {
                break;
            }

            var thisRoundMissing = new List<ResourceReference>();
            foreach (var (type, candidateIds) in candidatesByType)
            {
                HashSet<string> foundIds;
                try
                {
                    foundIds = await CheckExistingInAidboxAsync(httpClient, baseUrl, authHeader, type, candidateIds, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    inconclusiveTypes.Add(type);
                    continue;
                }

                foreach (var id in candidateIds)
                {
                    var reference = new ResourceReference(type, id);
                    if (foundIds.Contains(id))
                    {
                        present.Add(reference);
                    }
                    else
                    {
                        thisRoundMissing.Add(reference);
                    }
                }
            }

            if (thisRoundMissing.Count == 0)
            {
                break;
            }

            if (!autoFetchEnabled || fetchMissingReferenceAsync is null)
            {
                confirmedMissing.UnionWith(thisRoundMissing);
                break;
            }

            var newlyFetched = new List<MappedDestinationRecord>();
            foreach (var reference in thisRoundMissing)
            {
                if (autoFetchBudgetRemaining <= 0)
                {
                    confirmedMissing.Add(reference);
                    skippedDueToCapCount++;
                    continue;
                }

                autoFetchBudgetRemaining--;
                var (fetchedRecord, failureReason) = await TryFetchRecordForReferenceAsync(
                    reference, fetchMissingReferenceAsync, pipelineRunId, cancellationToken);
                if (fetchedRecord is null)
                {
                    confirmedMissing.Add(reference);
                    if (failureReason is not null)
                    {
                        fetchFailureReasons[reference] = failureReason;
                    }

                    continue;
                }

                newlyFetched.Add(fetchedRecord);
            }

            foreach (var fetchedRecord in newlyFetched)
            {
                // The actual recursive step: this fetched record's own references become next round's candidates.
                IndexRecord(fetchedRecord);
            }

            allFetchedRecords.AddRange(newlyFetched);
            frontier = newlyFetched;
        }

        var errors = new List<string>();

        // Fixed-point exclusion: a record referencing something confirmed-missing is excluded and its OWN identity
        // is added to confirmedMissing, so anything that in turn referenced it is caught on the next pass — this is
        // what makes an unresolvable nested reference (e.g. a fetched Encounter's own missing Practitioner) also
        // exclude the record that pulled that Encounter in to begin with, rather than leaving a dangling reference.
        var allCandidateRecords = records.Concat(allFetchedRecords).ToList();
        var excluded = new HashSet<MappedDestinationRecord>(ReferenceEqualityComparer.Instance);
        bool changed;
        do
        {
            changed = false;
            foreach (var record in allCandidateRecords)
            {
                if (excluded.Contains(record))
                {
                    continue;
                }

                var referenced = referencesByRecord.TryGetValue(record, out var refs) ? refs : [];
                var missing = referenced.Where(r => confirmedMissing.Contains(r)).Distinct().ToList();
                if (missing.Count == 0)
                {
                    continue;
                }

                excluded.Add(record);
                changed = true;
                if (identityByRecord.TryGetValue(record, out var ownIdentity))
                {
                    confirmedMissing.Add(ownIdentity);
                }

                var recordLabel = $"{record.ResourceType}/{record.SourceResourceId ?? "unknown"}";
                var missingLabel = string.Join(", ", missing.Select(m =>
                    fetchFailureReasons.TryGetValue(m, out var reason)
                        ? $"{m.Type}/{m.Id} (auto-fetch failed: {reason})"
                        : $"{m.Type}/{m.Id}"));
                errors.Add(
                    $"{recordLabel}: references {missingLabel}, which {(missing.Count == 1 ? "was" : "were")} not found in this " +
                    "batch, at the destination, or via auto-fetch — record was not written. Add the missing resource type to " +
                    "this destination's selected resources, or ensure it already exists at the destination before this run.");
            }
        } while (changed);

        var writable = allCandidateRecords.Where(r => !excluded.Contains(r)).ToList();

        foreach (var record in writable)
        {
            var referenced = referencesByRecord.TryGetValue(record, out var refs) ? refs : [];
            var inconclusive = referenced.Where(r => inconclusiveTypes.Contains(r.Type)).Distinct().ToList();
            if (inconclusive.Count > 0)
            {
                var recordLabel = $"{record.ResourceType}/{record.SourceResourceId ?? "unknown"}";
                var inconclusiveLabel = string.Join(", ", inconclusive.Select(m => $"{m.Type}/{m.Id}"));
                errors.Add(
                    $"{recordLabel}: could not verify whether {inconclusiveLabel} already exists at the destination " +
                    "(existence check failed) — record was written without confirming this reference resolves.");
            }
        }

        if (skippedDueToCapCount > 0)
        {
            errors.Add(
                $"Auto-fetch limit ({autoFetchMaxCount}) reached — {skippedDueToCapCount} additional missing " +
                "reference(s) were not attempted and remain blocked.");
        }

        return (writable, errors);
    }

    /// <summary>
    /// One <c>GET {Type}/{id}</c> attempt against whatever source EHR fed this run, via
    /// <see cref="PipelineWriteContext.FetchMissingReferenceAsync"/> — returns the fetched resource as a new
    /// <see cref="MappedDestinationRecord"/> on success, or null for anything that falls back to ordinary blocking:
    /// a null/empty response, unparseable JSON, or a <c>resourceType</c> that doesn't match what was asked for.
    /// </summary>
    private static async Task<(MappedDestinationRecord? Record, string? FailureReason)> TryFetchRecordForReferenceAsync(
        ResourceReference reference,
        Func<string, string, CancellationToken, Task<string?>> fetchMissingReferenceAsync,
        Guid pipelineRunId,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await fetchMissingReferenceAsync(reference.Type, reference.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                // A null/empty response means the source genuinely doesn't have it (e.g. a 404) — the fetch
                // delegate itself has no way to distinguish "not found" from other quiet failures, so this
                // case has no specific reason to report.
                return (null, null);
            }

            if (JsonNode.Parse(json) is not JsonObject fetchedResource
                || !string.Equals(fetchedResource["resourceType"]?.GetValue<string>(), reference.Type, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "fetched response did not match the expected resource type");
            }

            fetchedResource["id"] = reference.Id;
            return (new MappedDestinationRecord(
                pipelineRunId, reference.Type, reference.Type, reference.Id, new Dictionary<string, object?>(),
                fetchedResource.ToJsonString(JsonOptions)), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fetch/parse failed — the caller adds this reference to confirmedMissing, falling through to ordinary
            // blocking behavior, same as if auto-fetch had never been attempted, but the underlying reason (e.g.
            // "403 Forbidden" from the source EHR) is preserved so it doesn't look identical to a plain 404.
            return (null, exception.Message);
        }
    }

    /// <summary>
    /// For one resource type, checks which of <paramref name="candidateIds"/> already exist at the destination —
    /// standard FHIR search-OR syntax (<c>_id=a,b,c</c>), chunked to <see cref="ExistenceCheckChunkSize"/> ids per
    /// request. <c>_elements=id</c> asks the server to return minimal bodies; if a given destination doesn't honor
    /// it, the response still carries a full resource with an <c>id</c>, so this is a safe, non-critical hint rather
    /// than a hard dependency. Reuses the same auth header already resolved for the write itself — a destination
    /// whose credential is write-only (no read/search permission) will see every check fail here, which the caller
    /// treats as inconclusive (see <see cref="ResolveMissingReferencesAsync"/>), not as a hard error.
    /// </summary>
    private static async Task<HashSet<string>> CheckExistingInAidboxAsync(
        HttpClient httpClient,
        string baseUrl,
        System.Net.Http.Headers.AuthenticationHeaderValue? authHeader,
        string resourceType,
        IReadOnlyList<string> candidateIds,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chunk in candidateIds.Chunk(ExistenceCheckChunkSize))
        {
            var idParam = string.Join(",", chunk.Select(Uri.EscapeDataString));
            var endpoint = $"{baseUrl}/{resourceType}?_id={idParam}&_elements=id";

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (authHeader is not null)
            {
                request.Headers.Authorization = authHeader;
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body)
                || JsonNode.Parse(body) is not JsonObject searchSet
                || searchSet["entry"] is not JsonArray entries)
            {
                continue; // empty/no-match searchset — nothing found in this chunk
            }

            foreach (var entry in entries)
            {
                var id = entry?["resource"]?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    found.Add(id);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Builds one <c>Bundle</c> (<c>type: "batch"</c>) containing every entry, POSTs it once to the repository
    /// root, then correlates the response Bundle's entries back to <paramref name="entries"/> BY ARRAY INDEX — a
    /// batch Bundle preserves request order per the FHIR spec, and each entry's own <c>request.url</c> already pins
    /// the exact resource+id being written, so there's no need to parse an id back out of the response to know which
    /// record a given outcome belongs to. A transport-level failure (non-2xx on the outer POST) has no per-entry
    /// information to isolate against and fails the whole route, same as individual mode. A response/request entry
    /// count mismatch throws rather than risk zipping mismatched arrays and attributing the wrong error to the wrong
    /// record — a correctness risk, not a cosmetic one, in a healthcare pipeline.
    /// </summary>
    private static async Task WriteBundleAsync(
        HttpClient httpClient,
        string baseUrl,
        System.Net.Http.Headers.AuthenticationHeaderValue? authHeader,
        IReadOnlyList<(MappedDestinationRecord Record, string ResourceType, JsonObject Resource)> entries,
        List<string> recordErrors,
        List<string?> writtenResourceIds,
        CancellationToken cancellationToken)
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "batch",
            ["entry"] = new JsonArray(entries.Select(entry =>
            {
                var id = entry.Resource["id"]!.GetValue<string>();
                return (JsonNode)new JsonObject
                {
                    ["resource"] = entry.Resource,
                    ["request"] = new JsonObject
                    {
                        ["method"] = "PUT",
                        ["url"] = $"{entry.ResourceType}/{Uri.EscapeDataString(id)}"
                    }
                };
            }).ToArray())
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new StringContent(bundle.ToJsonString(JsonOptions), Encoding.UTF8, "application/fhir+json")
        };
        if (authHeader is not null)
        {
            request.Headers.Authorization = authHeader;
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"FHIR batch bundle POST to '{baseUrl}' failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var responseEntries = ParseBatchResponseEntries(responseBody);
        if (responseEntries.Count != entries.Count)
        {
            throw new InvalidOperationException(
                $"FHIR batch response entry count ({responseEntries.Count}) did not match request entry count " +
                $"({entries.Count}); cannot safely correlate results back to records.");
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var (record, resourceType, resource) = entries[i];
            var (isSuccess, diagnostics) = responseEntries[i];
            if (isSuccess)
            {
                writtenResourceIds.Add(record.SourceResourceId);
            }
            else
            {
                var id = resource["id"]?.GetValue<string>();
                var message = SafeErrorText.SanitizeOr(diagnostics, "FHIR destination rejected this resource.");
                recordErrors.Add($"{resourceType}/{record.SourceResourceId ?? id ?? "unknown"}: {message}");
            }
        }
    }

    /// <summary>
    /// Builds one <c>Bundle</c> (<c>type: "transaction"</c>) containing every entry, POSTs it once to the repository
    /// root. A transaction Bundle is atomic per the FHIR spec — the destination resolves every entry's references
    /// against the FULL set being written, not just what's already committed, so mutually-referencing records in the
    /// same write (e.g. an Observation's <c>hasMember</c> pointing at a sibling that <c>derivedFrom</c>-references it
    /// back) resolve correctly with no reordering needed — confirmed live against a real Aidbox instance. The
    /// tradeoff for that atomicity: unlike <see cref="WriteBundleAsync"/>'s per-entry isolation, a transaction either
    /// commits entirely or not at all — a non-success outer response means NONE of these entries were written, so
    /// this reports ONE aggregated failure for the whole write rather than per-record errors (there is nothing to
    /// isolate; every entry shares the identical fate). A transaction failure's <c>OperationOutcome</c> is the
    /// response body's own root object (unlike a batch response, where it's nested per-entry), so
    /// <see cref="ExtractOutcomeText"/> is called directly on the parsed response body.
    /// </summary>
    private static async Task WriteTransactionAsync(
        HttpClient httpClient,
        string baseUrl,
        System.Net.Http.Headers.AuthenticationHeaderValue? authHeader,
        IReadOnlyList<(MappedDestinationRecord Record, string ResourceType, JsonObject Resource)> entries,
        List<string> recordErrors,
        List<string?> writtenResourceIds,
        CancellationToken cancellationToken)
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(entries.Select(entry =>
            {
                var id = entry.Resource["id"]!.GetValue<string>();
                return (JsonNode)new JsonObject
                {
                    ["resource"] = entry.Resource,
                    ["request"] = new JsonObject
                    {
                        ["method"] = "PUT",
                        ["url"] = $"{entry.ResourceType}/{Uri.EscapeDataString(id)}"
                    }
                };
            }).ToArray())
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new StringContent(bundle.ToJsonString(JsonOptions), Encoding.UTF8, "application/fhir+json")
        };
        if (authHeader is not null)
        {
            request.Headers.Authorization = authHeader;
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var outcome = string.IsNullOrWhiteSpace(responseBody) ? null : JsonNode.Parse(responseBody) as JsonObject;
            var reason = SafeErrorText.SanitizeOr(
                ExtractOutcomeText(outcome), $"{(int)response.StatusCode} {response.ReasonPhrase}");
            var sample = entries.Take(5).Select(e => $"{e.ResourceType}/{e.Record.SourceResourceId ?? "unknown"}");
            var more = entries.Count > 5 ? $" (+{entries.Count - 5} more)" : string.Empty;
            recordErrors.Add(
                $"Transaction failed — none of the following {entries.Count} record(s) were written: " +
                $"{string.Join(", ", sample)}{more}. Reason: {reason}");
            return;
        }

        // Transaction succeeded as a whole — atomicity guarantees every entry was committed; no per-entry parsing needed.
        foreach (var entry in entries)
        {
            writtenResourceIds.Add(entry.Record.SourceResourceId);
        }
    }

    /// <summary>
    /// Reads a batch-response Bundle's <c>entry[].response.status</c> ("200 OK", "422 Unprocessable Entity", ...)
    /// and, for a failed entry, its <c>response.outcome</c> (an <c>OperationOutcome</c>) if the server populated one
    /// — not every FHIR server does, so this falls back to the raw status string rather than failing to parse.
    /// </summary>
    private static List<(bool IsSuccess, string? Diagnostics)> ParseBatchResponseEntries(string responseBody)
    {
        var results = new List<(bool, string?)>();

        if (string.IsNullOrWhiteSpace(responseBody)
            || JsonNode.Parse(responseBody) is not JsonObject responseBundle
            || responseBundle["entry"] is not JsonArray responseEntries)
        {
            return results;
        }

        foreach (var entryNode in responseEntries)
        {
            var status = entryNode?["response"]?["status"]?.GetValue<string>();
            var isSuccess = status is not null && status.TrimStart().StartsWith("2", StringComparison.Ordinal);

            string? diagnostics = null;
            if (!isSuccess)
            {
                var outcome = entryNode?["response"]?["outcome"] as JsonObject;
                diagnostics = ExtractOutcomeText(outcome);
                // Some servers (Aidbox included, observed in practice) return an OperationOutcome whose issue[]
                // carries none of the usual human-readable fields — just a bare status. Embedding the outcome's
                // own raw JSON (bounded) beats silently collapsing to just "422", which gives zero signal for
                // diagnosing *why* the destination rejected the resource.
                diagnostics ??= outcome is not null
                    ? $"{status} — {Truncate(outcome.ToJsonString(JsonOptions), 500)}"
                    : status;
            }

            results.Add((isSuccess, diagnostics));
        }

        return results;
    }

    /// <summary>
    /// Pulls the most human-readable text out of an <c>OperationOutcome</c>'s <c>issue[]</c> — servers disagree on
    /// which field carries it: some populate the plain-string <c>diagnostics</c>, others only <c>details</c> (a
    /// CodeableConcept, via its <c>.text</c> or a coding's <c>.display</c>), others only the bare <c>code</c>
    /// (e.g. <c>"invalid"</c>, <c>"not-found"</c>). Tries every issue, in that preference order, for the first
    /// non-empty value found. Defensive against a value existing under the expected key but not being a JSON
    /// string (an OperationOutcome is user/server content, not a shape this destination controls) — treated the
    /// same as "not present" rather than throwing.
    /// </summary>
    private static string? ExtractOutcomeText(JsonObject? outcome)
    {
        if (outcome?["issue"] is not JsonArray issues)
        {
            return null;
        }

        foreach (var issue in issues)
        {
            var diagnostics = TryGetString(issue?["diagnostics"]);
            if (!string.IsNullOrWhiteSpace(diagnostics))
            {
                return diagnostics;
            }

            var detailsText = TryGetString(issue?["details"]?["text"]);
            if (!string.IsNullOrWhiteSpace(detailsText))
            {
                return detailsText;
            }

            var codingDisplay = (issue?["details"]?["coding"] as JsonArray)?
                .Select(coding => TryGetString(coding?["display"]))
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (!string.IsNullOrWhiteSpace(codingDisplay))
            {
                return codingDisplay;
            }

            var code = TryGetString(issue?["code"]);
            if (!string.IsNullOrWhiteSpace(code))
            {
                return code;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>
    /// Produces a (resourceType, id, body) triple to PUT. Prefers the normalized FHIR resource; reconciles its
    /// <c>id</c> to a stable value so URL and body agree. Falls back to the flattened payload for non-FHIR flows.
    /// Shares <paramref name="resolvedResourceCache"/> with every other caller in this class so a record's FHIR
    /// resource (and any GUID minted for a missing id) is resolved exactly once.
    /// </summary>
    private static (string ResourceType, string Id, string Body) BuildFhirResource(
        MappedDestinationRecord record,
        Dictionary<MappedDestinationRecord, (string ResourceType, JsonObject Resource)?> resolvedResourceCache)
    {
        if (TryParseFhirResource(record, resolvedResourceCache, out var resourceType, out var resource))
        {
            var id = resource["id"]!.GetValue<string>();
            return (resourceType, Uri.EscapeDataString(id), resource.ToJsonString(JsonOptions));
        }

        // Non-FHIR fallback: post the flattened mapped payload to a permissive ingestion endpoint.
        var fallbackId = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? Guid.NewGuid().ToString("N")
            : Uri.EscapeDataString(record.SourceResourceId);
        return (record.ResourceType, fallbackId, MappedDestinationSerialization.ToJson(record));
    }

    private const string NumericIdNamespacePrefix = "fb-";

    /// <summary>Prefixes a purely-numeric logical id so a client may PUT-create it on a numeric-id-reserving server; leaves any id already containing a non-digit unchanged.</summary>
    private static string SafenNumericId(string id) =>
        IsAllDigits(id) ? NumericIdNamespacePrefix + id : id;

    /// <summary>
    /// Recursively rewrites every relative <c>"reference": "Type/{numericId}"</c> in the resource to
    /// <c>"Type/fb-{numericId}"</c>, matching <see cref="SafenNumericId"/> applied to the referenced resource's own
    /// id. Contained (<c>#</c>), logical (<c>urn:</c>), and absolute (<c>scheme://</c>) references — and references
    /// whose id already contains a non-digit — are left untouched.
    /// </summary>
    private static void RewriteNumericReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Key == "reference"
                        && property.Value is JsonValue value
                        && value.TryGetValue<string>(out var reference)
                        && SafenReference(reference) is { } rewritten
                        && !string.Equals(rewritten, reference, StringComparison.Ordinal))
                    {
                        obj[property.Key] = rewritten;
                    }
                    else
                    {
                        RewriteNumericReferences(property.Value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RewriteNumericReferences(item);
                }

                break;
        }
    }

    /// <summary>Returns the namespaced form of a relative <c>Type/{numericId}</c> reference, or the reference unchanged when it isn't one.</summary>
    private static string SafenReference(string reference)
    {
        if (string.IsNullOrEmpty(reference)
            || reference[0] == '#'
            || reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("://", StringComparison.Ordinal))
        {
            return reference;
        }

        var slash = reference.IndexOf('/');
        if (slash <= 0 || slash == reference.Length - 1)
        {
            return reference;
        }

        var resourceType = reference[..slash];
        var rest = reference[(slash + 1)..];
        // A relative reference can carry a version: Type/id/_history/vid — namespace only the id segment.
        var idEnd = rest.IndexOf('/');
        var id = idEnd < 0 ? rest : rest[..idEnd];
        var tail = idEnd < 0 ? string.Empty : rest[idEnd..];

        return IsAllLetters(resourceType) && IsAllDigits(id)
            ? $"{resourceType}/{NumericIdNamespacePrefix}{id}{tail}"
            : reference;
    }

    private static bool IsAllDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllLetters(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsLetter(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Shared by <see cref="BuildFhirResource"/>, the bundle-mode split, and reference resolution: parses
    /// <see cref="MappedDestinationRecord.SourceJson"/> as a FHIR resource and reconciles its <c>id</c> to a stable
    /// value (the resource's own <c>id</c> if present, else <see cref="MappedDestinationRecord.SourceResourceId"/>,
    /// else a new GUID) so every caller sees the exact same id this record will be written under. Memoized in
    /// <paramref name="resolvedResourceCache"/> — resolving more than once per record risks minting two different
    /// GUIDs for the same id-less record across different call sites. Returns false for the non-FHIR fallback flow
    /// (no <c>SourceJson</c>, or it doesn't parse as an object with a resourceType).
    /// </summary>
    private static bool TryParseFhirResource(
        MappedDestinationRecord record,
        Dictionary<MappedDestinationRecord, (string ResourceType, JsonObject Resource)?> resolvedResourceCache,
        out string resourceType,
        out JsonObject resource)
    {
        if (!resolvedResourceCache.TryGetValue(record, out var cached))
        {
            cached = ParseFhirResource(record);
            resolvedResourceCache[record] = cached;
        }

        if (cached is { } value)
        {
            resourceType = value.ResourceType;
            resource = value.Resource;
            return true;
        }

        resourceType = string.Empty;
        resource = null!;
        return false;
    }

    private static (string ResourceType, JsonObject Resource)? ParseFhirResource(MappedDestinationRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SourceJson)
            && JsonNode.Parse(record.SourceJson) is JsonObject parsed
            && parsed["resourceType"]?.GetValue<string>() is { Length: > 0 } type)
        {
            var id = parsed["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                id = string.IsNullOrWhiteSpace(record.SourceResourceId)
                    ? Guid.NewGuid().ToString("N")
                    : record.SourceResourceId;
                parsed["id"] = id;
            }

            StripVersionFromStableCodings(parsed);

            return (type, parsed);
        }

        return null;
    }

    /// <summary>
    /// Recursively strips <c>version</c> from any <c>Coding</c> in a <c>coding</c> array whose <c>system</c> is one of
    /// <see cref="StableCodeSystemVersions.StableCodeSystemUrls"/>, so a destination FHIR server (e.g. Aidbox) matches
    /// on <c>system</c> alone instead of rejecting a code purely because its version label doesn't match whatever
    /// CodeSystem version the destination has loaded. Leaves codings on any other system — including CodeSystems
    /// known to have real cross-version code drift — untouched.
    /// </summary>
    private static void StripVersionFromStableCodings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["coding"] is JsonArray codings)
                {
                    foreach (var coding in codings.OfType<JsonObject>())
                    {
                        var system = TryGetString(coding["system"]);
                        if (system is not null && StableCodeSystemVersions.StableCodeSystemUrls.Contains(system))
                        {
                            coding.Remove("version");
                        }
                    }
                }

                foreach (var child in obj)
                {
                    StripVersionFromStableCodings(child.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    StripVersionFromStableCodings(item);
                }

                break;
        }
    }

    /// <summary>
    /// Orders <paramref name="records"/> so any resource referenced by another (via any <c>"reference":
    /// "{Type}/{id}"</c> string found anywhere in the referencing resource's own FHIR JSON — the same universal
    /// shape FHIR uses for every reference field, so no per-resource-type field knowledge is needed) is written
    /// first — e.g. Organization/Practitioner before a Patient whose <c>generalPractitioner</c>/
    /// <c>managingOrganization</c> point at them, but ALSO a same-type reference like an Observation's
    /// <c>derivedFrom</c> pointing at another Observation in the same batch (e.g. Epic's multi-component vital-sign
    /// panels, where two granular Observations both reference a parent one). Ordering is resolved at exact
    /// <c>(Type, Id)</c> record granularity — via <see cref="TryParseFhirResource"/>'s already-reconciled id, the
    /// same one every other caller in this class (including the "present in batch" check in
    /// <see cref="ResolveMissingReferencesAsync"/>) uses — rather than at resource-type granularity, so ordering
    /// two records of the same type relative to each other is no different from ordering two different types.
    /// Derived from the batch's own JSON rather than a static FHIR-domain table: it only creates an ordering edge
    /// when the referenced (Type, Id) is actually present in this same batch — nothing to reorder against when it
    /// isn't (e.g. a Patient referencing an Organization that was never fetched from the source; that's a separate,
    /// upstream gap this can't fix — <see cref="ResolveMissingReferencesAsync"/> handles that case instead). Same
    /// Kahn/DFS topological sort shape as <c>DestinationNodeExecutors.OrderGroupsByReferenceDependency</c> uses for
    /// the relational-destination path (which instead keys off <see cref="MappedReferenceLookup.LookupTable"/> at
    /// resource-type granularity — a concept FHIR passthrough/customize records never populate, and relational
    /// tables don't need id-level ordering the way a single FHIR batch Bundle does) — cycle-safe, falling back to
    /// original order for records in a cycle (e.g. two mutually-referencing resources) rather than looping. Records
    /// with no <see cref="MappedDestinationRecord.SourceJson"/> (the non-FHIR fallback flow) have no extractable
    /// references and keep their original relative position.
    /// </summary>
    private static List<MappedDestinationRecord> OrderRecordsByFhirReferenceDependency(
        IReadOnlyCollection<MappedDestinationRecord> records,
        Dictionary<MappedDestinationRecord, (string ResourceType, JsonObject Resource)?> resolvedResourceCache)
    {
        var recordsList = records as IReadOnlyList<MappedDestinationRecord> ?? records.ToList();

        // Every record's own (Type, Id) identity — the SAME reconciled id it will actually be written under.
        var recordByReference = new Dictionary<ResourceReference, MappedDestinationRecord>();
        foreach (var record in recordsList)
        {
            if (TryParseFhirResource(record, resolvedResourceCache, out var resourceType, out var resource))
            {
                recordByReference[new ResourceReference(resourceType, resource["id"]!.GetValue<string>())] = record;
            }
        }

        // A record depends on whatever OTHER record in this batch its own references resolve to — exact (Type, Id)
        // match, not "same type present anywhere" — so a same-type reference is ordered just as precisely as a
        // cross-type one.
        var dependencies = new Dictionary<MappedDestinationRecord, List<MappedDestinationRecord>>(ReferenceEqualityComparer.Instance);
        foreach (var record in recordsList)
        {
            dependencies[record] = ExtractReferencedResources(record.SourceJson)
                .Where(reference => recordByReference.TryGetValue(reference, out var target) && !ReferenceEquals(target, record))
                .Select(reference => recordByReference[reference])
                .Distinct()
                .ToList();
        }

        var ordered = new List<MappedDestinationRecord>();
        var visited = new HashSet<MappedDestinationRecord>(ReferenceEqualityComparer.Instance);
        var visiting = new HashSet<MappedDestinationRecord>(ReferenceEqualityComparer.Instance);

        void Visit(MappedDestinationRecord record)
        {
            if (visited.Contains(record) || !visiting.Add(record))
            {
                return; // already ordered, or a cycle — stop recursing rather than looping forever
            }

            foreach (var dependency in dependencies[record])
            {
                Visit(dependency);
            }

            visiting.Remove(record);
            visited.Add(record);
            ordered.Add(record);
        }

        foreach (var record in recordsList)
        {
            Visit(record);
        }

        return ordered;
    }

    /// <summary>
    /// A resolved FHIR reference target, at exact <c>Type/id</c> granularity — deliberately more precise than a bare
    /// type string, since "Organization/XYZ exists in this batch" must never be treated as satisfying a reference to
    /// "Organization/ABC".
    /// </summary>
    private readonly record struct ResourceReference(string Type, string Id);

    /// <summary>
    /// Walks <paramref name="sourceJson"/> for every property literally named <c>"reference"</c> (FHIR's universal
    /// reference shape, e.g. <c>"subject": { "reference": "Patient/123" }</c>) at any depth, and returns the
    /// (Type, Id) pair for each one this mechanism can meaningfully resolve. Empty for null/unparseable JSON — the
    /// non-FHIR fallback flow has no references to extract. See <see cref="TryParseReference"/> for exactly which
    /// reference shapes are resolved and which are deliberately left alone.
    /// </summary>
    private static List<ResourceReference> ExtractReferencedResources(string? sourceJson)
    {
        var referencedResources = new List<ResourceReference>();
        if (string.IsNullOrWhiteSpace(sourceJson))
        {
            return referencedResources;
        }

        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            WalkForReferences(document.RootElement, referencedResources);
        }
        catch (JsonException)
        {
            // Not parseable FHIR JSON — nothing to extract, same as a missing SourceJson.
        }

        return referencedResources;
    }

    private static void WalkForReferences(JsonElement element, List<ResourceReference> referencedResources)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "reference", StringComparison.Ordinal)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var parsed = TryParseReference(property.Value.GetString());
                        if (parsed is { } reference)
                        {
                            referencedResources.Add(reference);
                        }
                    }
                    else
                    {
                        WalkForReferences(property.Value, referencedResources);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkForReferences(item, referencedResources);
                }
                break;
        }
    }

    /// <summary>
    /// Parses a FHIR <c>"reference"</c> string into the (Type, Id) pair this destination can existence-check
    /// against itself — returning null for shapes this mechanism deliberately does not resolve:
    /// <list type="bullet">
    /// <item>a contained-resource fragment (<c>"#comp1"</c>) — not independently addressable at the destination,
    /// only meaningful inside its parent resource;</item>
    /// <item>an absolute/external URL (contains <c>"://"</c>) — may legitimately point at a different FHIR server
    /// entirely; this destination's own base URL is never assumed to be the target;</item>
    /// <item>anything with no <c>'/'</c> at all, or an empty type/id segment.</item>
    /// </list>
    /// A trailing <c>/_history/{versionId}</c> (or any further path segment) is discarded — only the first two
    /// <c>'/'</c>-delimited segments are taken as Type and Id, so a versioned reference still matches the same
    /// (Type, Id) an unversioned one would.
    /// </summary>
    private static ResourceReference? TryParseReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference)
            || reference[0] == '#'
            || reference.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = reference.Split('/');
        if (segments.Length < 2 || segments[0].Length == 0 || segments[1].Length == 0)
        {
            return null;
        }

        return new ResourceReference(segments[0], segments[1]);
    }
}
