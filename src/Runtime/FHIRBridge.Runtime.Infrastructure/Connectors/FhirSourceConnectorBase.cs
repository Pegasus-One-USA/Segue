using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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
public abstract class FhirSourceConnectorBase : IFhirSourceClient
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
        var resources = new List<ResourceEnvelope>();
        var nextUrl = BuildSearchUrl(source.BaseUrl, resourceType, source.SearchCount, source.SearchParameters);
        var maxPages = source.MaxPages <= 0 ? 1 : source.MaxPages;

        for (var page = 0; page < maxPages && nextUrl is not null; page++)
        {
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

        var sourceKey = $"{source.TenantId?.ToString() ?? "none"}|{source.SourceConnectionId?.ToString() ?? source.ClientId ?? source.BaseUrl ?? "fhir"}";
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
        var message = $"{SourceDisplayName} request returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {requestUrl}.";

        if (string.IsNullOrWhiteSpace(body))
        {
            return message;
        }

        body = body.ReplaceLineEndings(" ").Trim();
        if (body.Length > 1000)
        {
            body = body[..1000] + "...";
        }

        return $"{message} Response body: {body}";
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
}
