using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
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
public abstract partial class FhirSourceConnectorBase : IFhirSourceClient
{
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

    public async Task<IReadOnlyList<ResourceEnvelope>> SearchAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            throw new InvalidOperationException($"{SourceDisplayName} base URL is required.");
        }

        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        var searchParameters = await ApplyPatientScopeAsync(resourceType, source, cancellationToken);
        var resources = new List<ResourceEnvelope>();
        var nextUrl = BuildSearchUrl(source.BaseUrl, resourceType, source.SearchCount, searchParameters);
        var maxPages = source.MaxPages <= 0 ? 1 : source.MaxPages;

        for (var page = 0; page < maxPages && nextUrl is not null; page++)
        {
            // Explicit, structured request log — .NET's default IHttpClientFactory request logging doesn't surface
            // the query string at the levels enabled here, and this is exactly what needs to be greppable in Seq
            // when verifying _count/_sort/_include/_revinclude/search-criteria actually reached the outbound call.
            _logger.LogInformation(
                "{Source} search request for {ResourceType} (page {Page}): {SearchUrl}",
                SourceDisplayName,
                resourceType,
                page + 1,
                nextUrl);

            using var response = await SendWithRetryAsync(nextUrl, accessToken, source, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = await BuildFailureMessageAsync(nextUrl, response, cancellationToken);
                throw new InvalidOperationException(message);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            resources.AddRange(FhirResourceParser.ParseSearchBundle(json));
            nextUrl = FhirResourceParser.GetNextLink(json);
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

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string requestUrl,
        string accessToken,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var maxRetryCount = Math.Max(0, _options.MaxRetryCount);
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetryCount; attempt++)
        {
            await WaitForSourceThrottleAsync(source, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.RequestTimeoutSeconds > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
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
                lastException = new TimeoutException($"{SourceDisplayName} request timed out after {_options.RequestTimeoutSeconds} seconds.");
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
    /// WorkflowExecutionContext). Without one of these, a provider such as Epic rejects an unscoped <c>Patient</c>
    /// search. The known patient targets its own resource by <c>_id</c>; every other resource type is filtered by
    /// <c>patient</c>. Caller-supplied parameters that already pin the patient (<c>patient</c>/<c>_id</c>/<c>subject</c>)
    /// are left untouched.
    /// </summary>
    private async Task<string?> ApplyPatientScopeAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var query = source.SearchParameters?.Trim().TrimStart('?') ?? string.Empty;
        var isPatientResource = string.Equals(resourceType, "Patient", StringComparison.OrdinalIgnoreCase);

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
        if (string.IsNullOrWhiteSpace(patientId))
        {
            return source.SearchParameters;
        }

        if (ContainsQueryParameter(query, "patient") ||
            ContainsQueryParameter(query, "subject") ||
            ContainsQueryParameter(query, "_id"))
        {
            return source.SearchParameters;
        }

        var scope = isPatientResource ? $"_id={patientId}" : $"patient={patientId}";
        return string.IsNullOrWhiteSpace(query) ? scope : $"{query}&{scope}";
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

    private async Task<string> BuildFailureMessageAsync(
        string requestUrl,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"{SourceDisplayName} request returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {RedactRequestUrl(requestUrl)}.";

        if (string.IsNullOrWhiteSpace(body))
        {
            return message;
        }

        body = RedactFailureBody(body.ReplaceLineEndings(" ").Trim());
        if (body.Length > 1000)
        {
            body = body[..1000] + "...";
        }

        return $"{message} Response body: {body}";
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
