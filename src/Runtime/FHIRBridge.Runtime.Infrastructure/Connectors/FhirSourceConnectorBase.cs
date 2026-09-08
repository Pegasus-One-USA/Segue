using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// Shared implementation for FHIR R4 REST source connectors: bearer-token acquisition, paginated <c>_count</c>
/// search, transient-status retry with jitter/Retry-After, and per-source request throttling. Concrete vendor
/// connectors (Epic, and — once re-enabled — Cerner, Allscripts, generic FHIR, …) derive from this and only
/// customize the small vendor-specific surface (<see cref="SourceDisplayName"/>, request headers, URL shape).
/// This is the "vendor inherits" axis of the Bridge model; the access-token grant (Backend / EHR-launch /
/// Standalone / Patient) is composed by the injected <see cref="IFhirAccessTokenProvider"/>.
/// </summary>
public abstract partial class FhirSourceConnectorBase : IFhirSourceClient, IResourceExtractionDiagnostics
{
    // Losses that did NOT fail the call: a rejected fan-out category, or a page cap cutting a fetch short.
    // The executor drains these into the run's skippedResourceTypes so the run reports PartialSuccess
    // rather than a clean success over a short result. Concurrent because the orchestrator runs resource
    // extractions with bounded parallelism.
    private readonly ConcurrentQueue<string> _incompleteReasons = new();

    private static readonly ConcurrentDictionary<string, SourceThrottle> SourceThrottles = new(StringComparer.Ordinal);
    private static readonly Random RetryJitter = new();
    private static readonly object RetryJitterLock = new();

    private readonly HttpClient _httpClient;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly EpicFhirClientOptions _options;
    private readonly ILogger _logger;

    protected FhirSourceConnectorBase(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        EpicFhirClientOptions? options = null,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _accessTokenProvider = accessTokenProvider;
        _options = options ?? new EpicFhirClientOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Human-readable source name used in log and error messages (e.g. "Epic FHIR").</summary>
    protected abstract string SourceDisplayName { get; }

    public IReadOnlyList<string> DrainIncompleteReasons()
    {
        if (_incompleteReasons.IsEmpty)
        {
            return [];
        }

        var reasons = new List<string>();
        while (_incompleteReasons.TryDequeue(out var reason))
        {
            reasons.Add(reason);
        }

        return reasons;
    }

    private void ReportIncomplete(string resourceType, string reason) =>
        _incompleteReasons.Enqueue($"{resourceType}: {reason}");

    public async Task<IReadOnlyList<ResourceEnvelope>> SearchAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            throw new InvalidOperationException($"{SourceDisplayName} base URL is required.");
        }

        // NOT redundant with the per-page acquisition in SearchPagesAsync, and not the token those requests use:
        // an interactive/launch grant carries the patient context in its token response, and ApplyPatientScopeAsync
        // below reads that context off the provider — so the exchange has to have happened before it runs. Cached,
        // so it costs a cache read.
        _ = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        var scopedSearchParameters = await ApplyPatientScopeAsync(resourceType, source, cancellationToken);
        scopedSearchParameters = MergeAdditionalQueryParameters(scopedSearchParameters, source);

        // Some default parameters (category, status) list several values for the resource type — Epic (confirmed;
        // likely other EHRs too) doesn't OR multiple comma-joined tokens together in one request the way a single
        // combined query would assume, and silently returns an empty bundle instead of erroring. Splitting into one
        // request per value and merging/deduping the results is the only way to actually get the full breadth of
        // data — see GetDefaultParameterValuesToSplit for which resource types/values this applies to.
        var valuesToSplit = GetDefaultParameterValuesToSplit(resourceType, scopedSearchParameters);
        if (valuesToSplit is null)
        {
            var searchParameters = ApplyDefaultSearchParameters(resourceType, scopedSearchParameters);
            searchParameters = ApplyAdditionalRequiredParameters(resourceType, searchParameters);
            return await SearchPagesAsync(resourceType, source, searchParameters, cancellationToken);
        }

        var seenResourceIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedResources = new List<ResourceEnvelope>();
        foreach (var (parameterName, value) in valuesToSplit)
        {
            var query = string.IsNullOrWhiteSpace(scopedSearchParameters)
                ? $"{parameterName}={value}"
                : $"{scopedSearchParameters}&{parameterName}={value}";
            query = ApplyAdditionalRequiredParameters(resourceType, query);

            IReadOnlyList<ResourceEnvelope> page;
            try
            {
                page = await SearchPagesAsync(resourceType, source, query, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One split value (e.g. one category token) rejected by the source shouldn't discard every other
                // value's results — Epic in particular can reject a single category/status token for reasons
                // unrelated to the rest of the split set (a business rule specific to that one code) while every
                // other value in the same list succeeds. Skip and keep going; the resource type as a whole is
                // still reported as at least partially successful instead of wholly failing on one bad value.
                _logger.LogWarning(
                    exception,
                    "{Source} search for {ResourceType} with {Parameter}={Value} failed and was skipped; " +
                    "continuing with the remaining values.",
                    SourceDisplayName, resourceType, parameterName, value);

                // Skipping the value keeps the other values' results, but whatever this one held is now missing
                // from the run. Record it so the run says so instead of reporting the short merge as complete.
                ReportIncomplete(
                    resourceType,
                    $"the '{parameterName}={value}' search was rejected and skipped, so any {resourceType} only " +
                    $"that value would have returned is missing — {exception.Message}");
                continue;
            }

            foreach (var resource in page)
            {
                // The FHIR id alone is enough to dedupe within one resource type — the same resource can legitimately
                // appear under more than one category value (e.g. an Observation tagged both "vital-signs" and
                // "smartdata").
                if (!string.IsNullOrWhiteSpace(resource.ResourceId) && !seenResourceIds.Add(resource.ResourceId))
                {
                    continue;
                }

                mergedResources.Add(resource);
            }
        }

        return mergedResources;
    }

    public async Task<ResourceEnvelope?> ReadByIdAsync(
        string resourceType,
        string id,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            throw new InvalidOperationException($"{SourceDisplayName} base URL is required.");
        }

        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        var requestUrl = $"{source.BaseUrl.TrimEnd('/')}/{resourceType}/{Uri.EscapeDataString(id)}";
        var readQuery = MergeAdditionalQueryParameters(null, source);
        if (!string.IsNullOrWhiteSpace(readQuery))
        {
            requestUrl = $"{requestUrl}?{readQuery}";
        }

        _logger.LogInformation("{Source} read request: {RequestUrl}", SourceDisplayName, requestUrl);

        using var response = await SendWithRetryAsync(requestUrl, accessToken, source, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(BuildFailureMessage(requestUrl, response, body));
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureFhirJsonBody(json, requestUrl, response, resourceType);

        return FhirResourceParser.ParseResource(json);
    }

    private async Task<IReadOnlyList<ResourceEnvelope>> SearchPagesAsync(
        string resourceType,
        FhirSourceConfiguration source,
        string? searchParameters,
        CancellationToken cancellationToken)
    {
        var resources = new List<ResourceEnvelope>();
        var nextUrl = BuildSearchUrl(source.BaseUrl, resourceType, source.SearchCount, searchParameters);
        var maxPages = source.MaxPages <= 0 ? 1 : source.MaxPages;

        // A "next" link is only worth following if it actually advances. eCW hands one out even when the page it
        // just served was complete, and following it re-serves the same records — so paging never ends on its own.
        // Both guards below detect that: the same URL coming round again, and a page that contributes no resource
        // id we have not already seen (a paginator that increments an offset it then ignores defeats the first
        // guard but not the second). A conforming server trips neither — its pages always bring new ids until it
        // drops the link. Without them the loop ran until the ACCESS TOKEN EXPIRED: a live eCW run made 31
        // successful Observation requests for 28 unique laboratory records, then 401'd on the six remaining
        // categories, which the caller's per-category skip swallowed — 28 records reported as a success.
        var requestedUrls = new HashSet<string>(StringComparer.Ordinal);
        var seenResourceIds = new HashSet<string>(StringComparer.Ordinal);
        var stoppedOnNonAdvancingPage = false;

        for (var page = 0; page < maxPages && nextUrl is not null; page++)
        {
            requestedUrls.Add(nextUrl);

            // Explicit, structured request log — .NET's default IHttpClientFactory request logging doesn't surface
            // the query string at the levels enabled here, and this is exactly what needs to be greppable in Seq
            // when verifying _count/_sort/_include/_revinclude/search-criteria actually reached the outbound call.
            _logger.LogInformation(
                "{Source} search request for {ResourceType} (page {Page}): {SearchUrl}",
                SourceDisplayName,
                resourceType,
                page + 1,
                nextUrl);


            // Re-read the token per page rather than once per resource-type fetch. The provider is cache-backed
            // (DistributedFhirAccessTokenCache: TTL = expiry minus a 1-minute skew), so this is a cache hit that
            // silently re-mints an expired token. Acquiring once meant any fetch outliving the token — eCW's is
            // ~5 minutes — started 401ing partway through with no way to recover.
            var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);

            using var response = await SendWithRetryAsync(nextUrl, accessToken, source, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = BuildFailureMessage(nextUrl, response, body);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new FHIRBridge.Runtime.Domain.Exceptions.ResourceAuthorizationException(
                        resourceType, (int)response.StatusCode, message);
                }

                if (IsNotSupportedOutcome(body))
                {
                    throw new FHIRBridge.Runtime.Domain.Exceptions.ResourceNotSupportedException(
                        resourceType, (int)response.StatusCode, message);
                }

                // Any other non-success response (e.g. a 400 because this resource type's search parameters
                // don't satisfy what the server requires) is isolated to this one resource type's request the
                // same way the two cases above are — skip just this type (or cancel the run, if it's the
                // cohort-seeding type) rather than failing the whole run over one resource type's bad request.
                throw new FHIRBridge.Runtime.Domain.Exceptions.ResourceRequestFailedException(
                    resourceType, (int)response.StatusCode, message);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureFhirJsonBody(json, nextUrl, response, resourceType);

            var newOnThisPage = 0;
            foreach (var resource in FhirResourceParser.ParseSearchBundle(json))
            {
                // An id-less resource can't be tracked for progress, so it's kept unconditionally and left for
                // the caller's own dedupe.
                if (string.IsNullOrWhiteSpace(resource.ResourceId) || seenResourceIds.Add(resource.ResourceId))
                {
                    resources.Add(resource);
                    newOnThisPage++;
                }
            }

            nextUrl = FhirResourceParser.GetNextLink(json);
            if (nextUrl is null)
            {
                break;
            }

            if (newOnThisPage == 0 || requestedUrls.Contains(nextUrl))
            {
                _logger.LogWarning(
                    "{Source} stopped paging {ResourceType} after {Pages} page(s) ({ResourceCount} records): the " +
                    "server offered a next link that does not advance ({Reason}). Treating the result as complete " +
                    "— following it further would re-fetch the same records until the access token expired.",
                    SourceDisplayName,
                    resourceType,
                    page + 1,
                    resources.Count,
                    newOnThisPage == 0 ? "the page repeated records already fetched" : "the link repeats a URL already fetched");

                stoppedOnNonAdvancingPage = true;
                nextUrl = null;
                break;
            }
        }

        // The loop can end two ways: the server stopped offering a next link (complete), or the page cap ran out
        // while one was still on offer (truncated). Those are NOT the same outcome, and returning the short list
        // unannounced is how a run reports 30 of 55 records as a success. Nothing downstream can tell the
        // difference from the list alone, so say so here — with the numbers needed to act on it.
        if (nextUrl is not null && !stoppedOnNonAdvancingPage)
        {
            _logger.LogWarning(
                "{Source} stopped paging {ResourceType} after {MaxPages} page(s) ({ResourceCount} records) while " +
                "the server was still offering more. This extraction is INCOMPLETE. Raise the connection's Max " +
                "Records Per Run (the page cap is derived from it), or leave it unset to page to completion.",
                SourceDisplayName,
                resourceType,
                maxPages,
                resources.Count);

            ReportIncomplete(
                resourceType,
                $"paging stopped at the {maxPages}-page cap with {resources.Count} record(s) while the server was " +
                "still offering more; raise the connection's Max Records Per Run, or leave it unset to page to completion");
        }

        return resources;
    }

    /// <summary>
    /// Applies the request headers each outbound FHIR call carries. The base adds the FHIR JSON <c>Accept</c>
    /// header; vendor connectors may override to add proprietary headers (Epic client id, Cerner tenant, …).
    /// </summary>
    protected virtual void ConfigureRequestHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
    }

    private static readonly IReadOnlyDictionary<string, string> NoAdditionalQueryParameters = new Dictionary<string, string>();

    /// <summary>
    /// Extra query parameters every outbound request (search and read-by-id alike) must carry — e.g. athenahealth's
    /// mandatory tenant-scoping <c>ah-practice</c> reference. The base adds none; vendor connectors override when
    /// their FHIR server requires request-level scoping beyond the standard search parameters.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> AdditionalQueryParameters(FhirSourceConfiguration source) =>
        NoAdditionalQueryParameters;

    /// <summary>
    /// Appends <see cref="AdditionalQueryParameters"/> onto an existing (possibly null) query string, skipping any
    /// key the caller already supplied. Used by both the search path (merged into the parameters that flow into
    /// <see cref="BuildSearchUrl"/>) and <see cref="ReadByIdAsync"/> (which otherwise builds no query string at all).
    /// </summary>
    private string? MergeAdditionalQueryParameters(string? searchParameters, FhirSourceConfiguration source)
    {
        var additional = AdditionalQueryParameters(source);
        if (additional.Count == 0)
        {
            return searchParameters;
        }

        var query = searchParameters?.Trim().TrimStart('?') ?? string.Empty;
        foreach (var (key, value) in additional)
        {
            if (ContainsQueryParameter(query, key))
            {
                continue;
            }

            var assignment = $"{key}={value}";
            query = string.IsNullOrWhiteSpace(query) ? assignment : $"{query}&{assignment}";
        }

        return string.IsNullOrWhiteSpace(query) ? null : query;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string requestUrl,
        string accessToken,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var maxRetryCount = Math.Max(0, _options.MaxRetryCount);
        Exception? lastException = null;

        // The connection's own Timeout (seconds) wins over the client-wide default, and applies to ONE request —
        // which is what the portal advertises it as ("Per-request timeout before the connector aborts and
        // retries"). It deliberately does NOT bound the whole paged extraction: a healthy source that simply
        // returns many pages must not be cut off mid-page, and total volume is already bounded by MaxPages/
        // MaxRecordsPerRun. Enforcing it here is what makes a stalled request — rather than a slow-but-progressing
        // one — the thing that gets cancelled.
        var requestTimeoutSeconds = source.TimeoutSeconds is { } configured && configured > 0
            ? configured
            : _options.RequestTimeoutSeconds;

        for (var attempt = 0; attempt <= maxRetryCount; attempt++)
        {
            await WaitForSourceThrottleAsync(source, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (requestTimeoutSeconds > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            ConfigureRequestHeaders(request);

            try
            {
                var response = await _httpClient.SendAsync(request, timeoutCts.Token);
                if (!IsTransient(response) || attempt == maxRetryCount)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                _logger.LogWarning(
                    "{Source} request returned transient status {StatusCode}. Retrying attempt {Attempt}/{MaxRetryCount} after {DelayMs} ms.",
                    SourceDisplayName,
                    (int)response.StatusCode,
                    attempt + 1,
                    maxRetryCount,
                    delay.TotalMilliseconds);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxRetryCount)
            {
                lastException = new TimeoutException($"{SourceDisplayName} request timed out after {requestTimeoutSeconds} seconds.");
                var delay = GetRetryDelay(null, attempt);
                _logger.LogWarning(
                    "{Source} request timed out. Retrying attempt {Attempt}/{MaxRetryCount} after {DelayMs} ms.",
                    SourceDisplayName,
                    attempt + 1,
                    maxRetryCount,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < maxRetryCount)
            {
                lastException = exception;
                var delay = GetRetryDelay(null, attempt);
                _logger.LogWarning(
                    exception,
                    "{Source} request failed transiently. Retrying attempt {Attempt}/{MaxRetryCount} after {DelayMs} ms.",
                    SourceDisplayName,
                    attempt + 1,
                    maxRetryCount,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"{SourceDisplayName} request exhausted {maxRetryCount} retries for {requestUrl}.",
            lastException);
    }

    private async Task WaitForSourceThrottleAsync(
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var minimumDelay = Math.Max(0, _options.MinimumMillisecondsBetweenRequestsPerSource);
        if (minimumDelay == 0)
        {
            return;
        }

        var sourceKey = source.SourceConnectionId?.ToString() ?? source.ClientId ?? source.BaseUrl ?? "fhir";
        var throttle = SourceThrottles.GetOrAdd(sourceKey, _ => new SourceThrottle());

        await throttle.Gate.WaitAsync(cancellationToken);
        try
        {
            var remaining = TimeSpan.FromMilliseconds(minimumDelay) - (DateTimeOffset.UtcNow - throttle.LastRequestOnUtc);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }

            throttle.LastRequestOnUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            throttle.Gate.Release();
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is { } retryAfterDelta)
        {
            return ClampDelay(retryAfterDelta);
        }

        if (response?.Headers.RetryAfter?.Date is { } retryAfterDate)
        {
            var retryAfterDateDelay = retryAfterDate - DateTimeOffset.UtcNow;
            if (retryAfterDateDelay > TimeSpan.Zero)
            {
                return ClampDelay(retryAfterDateDelay);
            }
        }

        int jitterMs;
        lock (RetryJitterLock)
        {
            jitterMs = RetryJitter.Next(0, Math.Max(1, _options.MaxRetryJitterMilliseconds));
        }

        var baseDelayMs = Math.Max(100, _options.BaseRetryDelayMilliseconds);
        var exponentialDelayMs = baseDelayMs * Math.Pow(2, attempt);

        return ClampDelay(TimeSpan.FromMilliseconds(exponentialDelayMs + jitterMs));
    }

    private TimeSpan ClampDelay(TimeSpan delay)
    {
        var maximum = TimeSpan.FromSeconds(Math.Max(1, _options.MaxRetryDelaySeconds));
        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > maximum ? maximum : delay;
    }

    private static bool IsTransient(HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;

        return response.StatusCode == HttpStatusCode.RequestTimeout ||
               response.StatusCode == (HttpStatusCode)429 ||
               statusCode >= 500;
    }

    /// <summary>
    /// Scopes the search to a known patient — the SMART launch's own context when the token grant carries one
    /// (<c>launch/patient</c> flows), falling back to the request-time <see cref="FhirSourceConfiguration.TargetPatientId"/>
    /// otherwise (e.g. a patient the caller picked from a prior name search, via WorkflowRunRequest.PatientId — see
    /// WorkflowExecutionContext). Without one of these, falls back to <see cref="FhirSourceConfiguration.PatientIds"/>
    /// — a cohort of patient ids (e.g. discovered by this same workflow run's own Patient extraction, threaded in by
    /// <c>SourceNodeExecutor</c>) OR'd together via a comma-separated reference list. Without either, a provider such
    /// as Epic rejects an unscoped <c>Patient</c> search but every other resource type is left unscoped (pre-existing
    /// behavior — see <c>No_patient_context_leaves_the_query_unscoped</c>). The known patient(s) target their own
    /// resource(s) by <c>_id</c>; every other patient-compartment resource type is filtered by <c>patient</c>.
    /// Resource types outside the patient compartment entirely (see <see cref="PatientCompartmentResourceTypes"/> —
    /// Practitioner, Organization, Location, ...) reach this method with a clean, already-scrubbed source
    /// (<c>SourceNodeExecutors</c> clears SearchParameters/PatientIds/PatientSearchCriteria before calling in for
    /// these), since a <c>patient=</c> search parameter is never meaningful for them — the branch below passes
    /// their (already-null) SearchParameters through untouched, producing a single bare, unscoped request.
    /// Caller-supplied parameters that already pin the patient (<c>patient</c>/<c>_id</c>/<c>subject</c>) are left
    /// untouched.
    /// </summary>
    private async Task<string?> ApplyPatientScopeAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var query = source.SearchParameters?.Trim().TrimStart('?') ?? string.Empty;
        var isPatientResource = string.Equals(resourceType, "Patient", StringComparison.OrdinalIgnoreCase);
        // Device is not a genuine FHIR-spec patient-compartment member (see PatientCompartmentResourceTypes' own
        // doc comment) — but Epic's own business-rule validator (code 59108, "A patient is required") rejects an
        // unscoped Device search anyway. Scoped as an Epic-only addition here rather than added to the shared
        // domain-level list, since that list is also relied on elsewhere for spec-accurate compartment membership.
        var requiresEpicPatientScopeBeyondCompartment = source.SourceType == RuntimeSourceType.Epic
            && string.Equals(resourceType, "Device", StringComparison.OrdinalIgnoreCase);
        var isCompartmentResource = isPatientResource
            || PatientCompartmentResourceTypes.IsSupported(resourceType)
            || requiresEpicPatientScopeBeyondCompartment;

        if (!isCompartmentResource)
        {
            // Non-patient-compartment types can't be patient-scoped — connection-level
            // FhirSourceConfiguration.SearchParameters pass through untouched (SourceNodeExecutors already scrubs
            // any leftover Patient-search criteria before calling in for one of these, so this is typically null).
            return source.SearchParameters;
        }

        // A request-time raw search criteria string (e.g. "active=true", "identifier=MRN12345",
        // "family=Smith&given=John", "birthdate=1990-01-01" — from a third-party app's own free-text search box,
        // threaded via WorkflowRunRequest → WorkflowExecutionContext) takes precedence over any launched-patient
        // context for the Patient resource type only — the caller is explicitly filtering by their own criteria,
        // possibly not the same patient (or not yet knowing which one) last logged in via an interactive launch, so
        // auto-scoping to that launch's _id would silently ignore the search the caller asked for. Passed through
        // as-is (not parsed/validated) — any FHIR search parameter the target server accepts is valid here.
        if (isPatientResource && !string.IsNullOrWhiteSpace(source.PatientSearchCriteria))
        {
            var criteria = source.PatientSearchCriteria.Trim().TrimStart('?').TrimStart('&');
            return string.IsNullOrWhiteSpace(query) ? criteria : $"{query}&{criteria}";
        }

        string? patientId = null;
        if (_accessTokenProvider is IFhirPatientContextProvider patientContextProvider)
        {
            patientId = await patientContextProvider.GetPatientContextAsync(source, cancellationToken);
        }

        patientId ??= source.TargetPatientId;

        var alreadyScoped = ContainsQueryParameter(query, "patient") ||
            ContainsQueryParameter(query, "subject") ||
            ContainsQueryParameter(query, "_id");

        if (string.IsNullOrWhiteSpace(patientId))
        {
            if (alreadyScoped || source.PatientIds is not { Count: > 0 } cohort)
            {
                return source.SearchParameters;
            }

            var cohortIdList = string.Join(',', cohort);
            var cohortScope = isPatientResource ? $"_id={cohortIdList}" : $"patient={cohortIdList}";
            return string.IsNullOrWhiteSpace(query) ? cohortScope : $"{query}&{cohortScope}";
        }

        if (alreadyScoped)
        {
            return source.SearchParameters;
        }

        var scope = isPatientResource ? $"_id={patientId}" : $"patient={patientId}";
        return string.IsNullOrWhiteSpace(query) ? scope : $"{query}&{scope}";
    }

    /// <summary>
    /// Epic (enforcing the underlying US Core profile) rejects an <c>Observation</c> or <c>Condition</c> search with
    /// neither <c>category</c> nor <c>code</c> present — "Must have either code or category." This table defaults
    /// every standard US Core category for the resource types that require one, so an otherwise-unscoped fetch still
    /// succeeds and returns the full breadth of a patient's data, rather than requiring every caller to separately
    /// know and supply this Epic/US-Core-specific requirement. Medication resource types default to a <c>status</c>
    /// filter instead, to avoid Epic returning an unbounded/ambiguous set of historical orders. A caller-supplied
    /// parameter of the same kind (however it reached <paramref name="searchParameters"/> — connection-level
    /// SearchParameters, PatientSearchCriteria, etc.) is left untouched. Resource types with no entry here are
    /// unaffected — registry lookup, not a switch/if-chain, so adding a new default is a table entry, not a branch.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> EpicDefaultSearchParametersByResourceType =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Observation"] = ("category", "social-history,vital-signs,imaging,laboratory,procedure,survey,exam,therapy,activity,smartdata,core-characteristics"),
            // The original 4-category default silently missed real US-Core data Epic categorizes under the other
            // four (infection, medical-history, reason-for-visit, dental) — a Condition search scoped to only the
            // first four never surfaces them at all, regardless of the single-request-vs-split-per-value fix below.
            ["Condition"] = ("category", "problem-list-item,health-concern,encounter-diagnosis,genomics,infection,medical-history,reason-for-visit,dental"),
            ["MedicationRequest"] = ("status", "active,completed,stopped"),
            ["MedicationAdministration"] = ("status", "completed,in-progress,stopped"),
            // Epic rejects CarePlan searches with no category at all (business-rule 59159); cover every
            // category Epic documents so an uncategorized-in-code-but-valid-in-Epic plan is never dropped.
            ["CarePlan"] = ("category", "38717003,734163000,736271009,736353004,738906000,736378000,719091000000102,inpatient-pathway,409073007,care-path"),
        };

    /// <summary>
    /// Per-resource-type default search parameter, applied only when the caller hasn't already supplied that
    /// parameter (or <c>code</c>) themselves — registry lookup, not a switch/if-chain, so adding a new default is a
    /// table entry. The base table encodes Epic's US-Core category/status requirements; vendor connectors with
    /// different per-resource requirements (e.g. athenahealth's <c>MedicationRequest</c> needing <c>intent=order</c>
    /// instead) override this property with their own table.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> DefaultSearchParametersByResourceType =>
        EpicDefaultSearchParametersByResourceType;

    /// <summary>
    /// A second, non-splitting required parameter for a resource type that already has a splitting default above
    /// — e.g. CarePlan needs both a category (business-rule 59159, split per value — see
    /// <see cref="DefaultSearchParametersByResourceType"/>) AND a date (business-rule 59108: "activity-date has not
    /// been provided") on every request. Unlike category/status, a date requirement has no enum of values to split
    /// by — Epic only requires it be present — so it's applied once, unconditionally, alongside whichever category
    /// value a given split request is using, rather than being another axis to split on.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> EpicAdditionalRequiredParametersByResourceType =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            // ge1900-01-01 is a deliberately wide-open lower bound — satisfies Epic's presence requirement
            // (its own internal name for this parameter is "activity-date") without narrowing the result the
            // way a real, meaningful date range would.
            ["CarePlan"] = ("date", "ge1900-01-01"),
        };

    /// <summary>See <see cref="EpicAdditionalRequiredParametersByResourceType"/>. Vendor connectors with different
    /// per-resource requirements override this property with their own table, same as
    /// <see cref="DefaultSearchParametersByResourceType"/>.</summary>
    protected virtual IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> AdditionalRequiredParametersByResourceType =>
        EpicAdditionalRequiredParametersByResourceType;

    /// <summary>Unconditionally appends this resource type's additional required parameter (see
    /// <see cref="AdditionalRequiredParametersByResourceType"/>) when the caller hasn't already supplied it —
    /// applied to both the non-split path and every individual request the category/status split produces, since
    /// it's a presence requirement independent of whatever value is being split on.</summary>
    private string? ApplyAdditionalRequiredParameters(string resourceType, string? searchParameters)
    {
        if (!AdditionalRequiredParametersByResourceType.TryGetValue(resourceType, out var required))
        {
            return searchParameters;
        }

        var query = searchParameters?.Trim().TrimStart('?') ?? string.Empty;
        if (ContainsQueryParameter(query, required.ParameterName))
        {
            return searchParameters;
        }

        var assignment = $"{required.ParameterName}={required.DefaultValue}";
        return string.IsNullOrWhiteSpace(query) ? assignment : $"{query}&{assignment}";
    }

    private string? ApplyDefaultSearchParameters(string resourceType, string? searchParameters)
    {
        if (!DefaultSearchParametersByResourceType.TryGetValue(resourceType, out var defaultParameter))
        {
            return searchParameters;
        }

        var query = searchParameters?.Trim().TrimStart('?') ?? string.Empty;
        if (ContainsQueryParameter(query, defaultParameter.ParameterName) ||
            ContainsQueryParameter(query, "code"))
        {
            return searchParameters;
        }

        var defaultAssignment = $"{defaultParameter.ParameterName}={defaultParameter.DefaultValue}";
        return string.IsNullOrWhiteSpace(query) ? defaultAssignment : $"{query}&{defaultAssignment}";
    }

    /// <summary>
    /// Returns one <c>(parameterName, value)</c> pair per value in this resource type's default parameter (see
    /// <see cref="DefaultSearchParametersByResourceType"/>), so <see cref="SearchAsync"/> can issue a separate
    /// request per value and merge the results — instead of one request with every value comma-joined, which Epic
    /// (and potentially other EHRs) doesn't OR together the way a single combined query would assume. Returns null
    /// when this resource type has no default parameter, or the caller already supplied that parameter (or
    /// <c>code</c>) themselves — same "don't override caller-supplied criteria" rule <see cref="ApplyDefaultSearchParameters"/>
    /// already follows, just checked here first since the split path bypasses that method entirely.
    /// </summary>
    private IReadOnlyList<(string ParameterName, string Value)>? GetDefaultParameterValuesToSplit(
        string resourceType, string? searchParameters)
    {
        if (!DefaultSearchParametersByResourceType.TryGetValue(resourceType, out var defaultParameter))
        {
            return null;
        }

        var query = searchParameters?.Trim().TrimStart('?') ?? string.Empty;
        if (ContainsQueryParameter(query, defaultParameter.ParameterName) ||
            ContainsQueryParameter(query, "code"))
        {
            return null;
        }

        var values = defaultParameter.DefaultValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return values.Select(value => (defaultParameter.ParameterName, value)).ToList();
    }

    /// <summary>
    /// Builds the paginated search URL. The base honors caller-supplied search parameters and injects a
    /// <c>_count</c> when absent; vendor connectors may override for proprietary URL shapes.
    /// </summary>
    protected virtual string BuildSearchUrl(
        string baseUrl,
        string resourceType,
        int searchCount,
        string? searchParameters)
    {
        var count = searchCount <= 0 ? 100 : searchCount;
        var query = string.IsNullOrWhiteSpace(searchParameters)
            ? string.Empty
            : searchParameters.Trim().TrimStart('?');

        if (!ContainsQueryParameter(query, "_count"))
        {
            query = string.IsNullOrWhiteSpace(query)
                ? $"_count={count}"
                : $"{query}&_count={count}";
        }

        return $"{baseUrl.TrimEnd('/')}/{resourceType}?{query}";
    }

    /// <summary>
    /// Guards the success path against a 2xx response whose body isn't FHIR JSON at all. Servers fronted by a
    /// gateway, WAF, SSO proxy or plain misconfiguration answer 200 with an HTML sign-in or error page, and a FHIR
    /// server that ignores the <c>Accept: application/fhir+json</c> header answers with XML — both start with
    /// '&lt;', so handing the body to the parser produced a bare
    /// <c>JsonReaderException: '&lt;' is an invalid start of a value</c> naming neither the source, the resource
    /// type, the URL, nor the content type. Thrown as a
    /// <see cref="FHIRBridge.Runtime.Domain.Exceptions.ResourceRequestFailedException"/> so it is isolated to this
    /// one resource type and excluded from the executor's retry loop, exactly like the non-success statuses above —
    /// re-requesting will not turn an HTML page into a Bundle.
    /// </summary>
    private void EnsureFhirJsonBody(
        string body,
        string requestUrl,
        HttpResponseMessage response,
        string resourceType)
    {
        var trimmed = body?.TrimStart();
        if (!string.IsNullOrEmpty(trimmed) && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            return;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var cause = string.IsNullOrEmpty(trimmed)
            ? "the response body was empty"
            : trimmed[0] == '<'
                ? trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                  trimmed.Contains("http://hl7.org/fhir", StringComparison.OrdinalIgnoreCase)
                    ? "the server returned FHIR XML instead of JSON — it is ignoring the Accept: application/fhir+json " +
                      "header, so add _format=json to the connection's additional query parameters"
                    : "the server returned an HTML page instead of FHIR JSON — the base URL is most likely not a FHIR " +
                      "R4 endpoint (check for a trailing path such as /fhir/R4), or a gateway/SSO proxy answered the " +
                      "request with a sign-in or error page"
                : "the response body was not JSON";

        var redactedBody = RedactFailureBody(trimmed?.ReplaceLineEndings(" ").Trim() ?? string.Empty);
        if (redactedBody.Length > 500)
        {
            redactedBody = redactedBody[..500] + "...";
        }

        var message =
            $"{SourceDisplayName} returned {(int)response.StatusCode} ({response.ReasonPhrase}) with content type " +
            $"'{contentType ?? "(none)"}' for {RedactRequestUrl(requestUrl)}, but {cause}. " +
            $"Response body: {redactedBody}";

        _logger.LogError(
            "{Source} {ResourceType} request succeeded with a non-JSON body (content type {ContentType}): {Reason}",
            SourceDisplayName,
            resourceType,
            contentType ?? "(none)",
            cause);

        throw new FHIRBridge.Runtime.Domain.Exceptions.ResourceRequestFailedException(
            resourceType, (int)response.StatusCode, message);
    }

    private string BuildFailureMessage(
        string requestUrl,
        HttpResponseMessage response,
        string body)
    {
        var message = $"{SourceDisplayName} request returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {RedactRequestUrl(requestUrl)}.";

        if (string.IsNullOrWhiteSpace(body))
        {
            return message;
        }

        var redactedBody = RedactFailureBody(body.ReplaceLineEndings(" ").Trim());
        if (redactedBody.Length > 1000)
        {
            redactedBody = redactedBody[..1000] + "...";
        }

        return $"{message} Response body: {redactedBody}";
    }

    /// <summary>
    /// True when the failed response body is a FHIR <c>OperationOutcome</c> carrying a <c>not-supported</c> issue
    /// code — e.g. Epic returning 400 for a resource type the tenant's app registration doesn't expose. Distinct
    /// from a 401/403 (this app isn't authorized) so callers can isolate "this resource type doesn't exist here"
    /// without treating it as a transient failure worth retrying.
    /// </summary>
    private static bool IsNotSupportedOutcome(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("issue", out var issues) ||
                issues.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return false;
            }

            foreach (var issue in issues.EnumerateArray())
            {
                if (issue.TryGetProperty("code", out var code) &&
                    code.ValueKind == System.Text.Json.JsonValueKind.String &&
                    string.Equals(code.GetString(), "not-supported", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string RedactRequestUrl(string requestUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
        {
            return RedactFailureBody(requestUrl);
        }

        return string.IsNullOrWhiteSpace(uri.Query)
            ? requestUrl
            : $"{uri.GetLeftPart(UriPartial.Path)}?[redacted]";
    }

    private static string RedactFailureBody(string value)
    {
        var redacted = PatientQueryParameterRegex().Replace(value, "$1=[redacted]");
        redacted = PatientJsonPropertyRegex().Replace(redacted, "$1\"[redacted]\"");
        return IdJsonPropertyRegex().Replace(redacted, "$1\"[redacted]\"");
    }

    private static bool ContainsQueryParameter(string query, string parameterName)
    {
        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part =>
            {
                var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
                var name = equalsIndex < 0 ? part : part[..equalsIndex];

                return string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase);
            });
    }

    private sealed class SourceThrottle
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastRequestOnUtc { get; set; } = DateTimeOffset.MinValue;
    }

    [GeneratedRegex(@"(?i)\b(patient|subject|_id)=([^&\s""'}]+)")]
    private static partial Regex PatientQueryParameterRegex();

    [GeneratedRegex(@"(?i)(""patient""\s*:\s*)""[^""]+""")]
    private static partial Regex PatientJsonPropertyRegex();

    [GeneratedRegex(@"(?i)(""id""\s*:\s*)""[^""]+""")]
    private static partial Regex IdJsonPropertyRegex();
}
