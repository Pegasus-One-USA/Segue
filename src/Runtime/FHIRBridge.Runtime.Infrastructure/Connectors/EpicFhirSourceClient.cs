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
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

public sealed class EpicFhirSourceClient : IFhirSourceClient
{
    private static readonly ConcurrentDictionary<string, SourceThrottle> SourceThrottles = new(StringComparer.Ordinal);
    private static readonly Random RetryJitter = new();
    private static readonly object RetryJitterLock = new();

    private readonly HttpClient _httpClient;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly EpicFhirClientOptions _options;
    private readonly ILogger<EpicFhirSourceClient> _logger;

    public EpicFhirSourceClient(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        IOptions<EpicFhirClientOptions>? options = null,
        ILogger<EpicFhirSourceClient>? logger = null)
    {
        _httpClient = httpClient;
        _accessTokenProvider = accessTokenProvider;
        _options = options?.Value ?? new EpicFhirClientOptions();
        _logger = logger ?? NullLogger<EpicFhirSourceClient>.Instance;
    }

    public async Task<IReadOnlyList<ResourceEnvelope>> SearchAsync(
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            throw new InvalidOperationException("Epic FHIR base URL is required.");
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
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

            try
            {
                var response = await _httpClient.SendAsync(request, timeoutCts.Token);
                if (!IsTransient(response) || attempt == maxRetryCount)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                _logger.LogWarning(
                    "Epic FHIR request returned transient status {StatusCode}. Retrying attempt {Attempt}/{MaxRetryCount} after {DelayMs} ms.",
                    (int)response.StatusCode,
                    attempt + 1,
                    maxRetryCount,
                    delay.TotalMilliseconds);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxRetryCount)
            {
                lastException = new TimeoutException($"Epic FHIR request timed out after {_options.RequestTimeoutSeconds} seconds.");
                var delay = GetRetryDelay(null, attempt);
                _logger.LogWarning(
                    "Epic FHIR request timed out. Retrying attempt {Attempt}/{MaxRetryCount} after {DelayMs} ms.",
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
                    "Epic FHIR request failed transiently. Retrying attempt {Attempt}/{MaxRetryCount} after {DelayMs} ms.",
                    attempt + 1,
                    maxRetryCount,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Epic FHIR request exhausted {maxRetryCount} retries for {requestUrl}.",
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

        var sourceKey = $"{source.TenantId?.ToString() ?? "none"}|{source.SourceConnectionId?.ToString() ?? source.ClientId ?? source.BaseUrl ?? "epic"}";
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

    private static string BuildSearchUrl(
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

    private static async Task<string> BuildFailureMessageAsync(
        string requestUrl,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"Epic FHIR request returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {requestUrl}.";

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
