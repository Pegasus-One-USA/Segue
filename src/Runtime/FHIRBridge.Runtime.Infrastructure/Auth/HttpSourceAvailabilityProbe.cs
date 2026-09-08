using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// <see cref="ISourceAvailabilityProbe"/> over an unauthenticated <c>GET {baseUrl}/metadata</c>.
/// <para>
/// Only response HEADERS are read (<see cref="HttpCompletionOption.ResponseHeadersRead"/>, body never consumed) —
/// a CapabilityStatement is a large document and none of it is needed to answer "did anything answer".
/// </para>
/// <para>
/// The verdict is cached briefly per host in the shared distributed cache. Without that, this would add a round
/// trip to every scheduled run of every workflow, and would fire repeatedly inside a single run: the DAG extracts
/// up to four resource types concurrently, and a warm token cache means many runs currently reach the vendor zero
/// times. The cache is keyed by host, not by connection, because availability is a property of the host — ten
/// workflows against the same Epic instance should cost one probe.
/// </para>
/// </summary>
public sealed class HttpSourceAvailabilityProbe : ISourceAvailabilityProbe
{
    /// <summary>
    /// Short on purpose. A probe that hangs is worse than no probe: it would delay every run by the outbound
    /// HttpClient's default budget while adding nothing. Applied as a linked CancellationTokenSource rather than
    /// relying on the client's own Timeout so it holds regardless of what resilience handlers the composing host
    /// has layered on this client.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Long enough that a burst of workflow starts shares one probe, short enough that a recovered vendor is
    /// picked up on the next run rather than staying "down" for minutes.
    /// </summary>
    private static readonly TimeSpan VerdictCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly IDistributedCache? _cache;
    private readonly ILogger _logger;

    public HttpSourceAvailabilityProbe(
        HttpClient httpClient,
        IDistributedCache? cache = null,
        ILogger<HttpSourceAvailabilityProbe>? logger = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<SourceAvailabilityResult> CheckAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Nothing probeable. Unknown, never Down — a connection with no usable base URL will fail its real
            // call with a much better message than anything this probe could invent.
            return SourceAvailabilityResult.Unknown("No absolute http(s) base URL to probe.");
        }

        var cacheKey = BuildCacheKey(uri);
        var cached = await TryReadCachedAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var result = await ProbeAsync(baseUrl!, cancellationToken);

        // Unknown is never cached: it says "we failed to measure", and caching that would suppress a real
        // measurement for the next 30 seconds for no benefit.
        if (result.Availability != SourceAvailability.Unknown)
        {
            await TryWriteCachedAsync(cacheKey, result, cancellationToken);
        }

        return result;
    }

    private async Task<SourceAvailabilityResult> ProbeAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var metadataUrl = $"{baseUrl.TrimEnd('/')}/metadata";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            return ClassifyStatus((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The RUN was cancelled, not the vendor failing. Unknown, so the caller proceeds and observes the
            // cancellation from its own real call rather than seeing it reported as an outage.
            return SourceAvailabilityResult.Unknown("Availability probe cancelled by the caller.");
        }
        catch (OperationCanceledException)
        {
            // Our own ProbeTimeout elapsed: nothing answered within the budget.
            return SourceAvailabilityResult.Down(
                $"No response from {DescribeHost(metadataUrl)} within {ProbeTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException exception)
        {
            // The core signal: DNS failure, connection refused, TLS failure, connection reset. The inner socket
            // message is transport-level ("No such host is known") — never an upstream response body — so it is
            // safe to surface.
            var reason = (exception.InnerException?.Message ?? exception.Message).ReplaceLineEndings(" ").Trim();
            return SourceAvailabilityResult.Down($"Could not connect to {DescribeHost(metadataUrl)}: {reason}");
        }
        catch (Exception exception) when (IsOpenCircuit(exception))
        {
            // A tripped circuit breaker means repeated recent failures against this host already — treat it as the
            // outage it is, rather than letting the run wait to rediscover that. Matched by type NAME so this
            // assembly needs no direct Polly reference.
            return SourceAvailabilityResult.Down(
                $"Recent repeated failures against {DescribeHost(metadataUrl)} have opened the circuit breaker.");
        }
        catch (Exception exception)
        {
            // Anything unanticipated is a failure to MEASURE, not evidence of an outage — see the never-return-Down
            // -on-a-doubt contract on ISourceAvailabilityProbe.
            _logger.LogDebug(
                exception,
                "Source availability probe was inconclusive for {MetadataUrl}.",
                metadataUrl);
            return SourceAvailabilityResult.Unknown("The availability probe could not complete.");
        }
    }

    /// <summary>
    /// The crux of the whole feature: a 4xx counts as UP. A server that answers 401/403/404 has received and
    /// processed the request, so it is demonstrably running and a later auth failure is a genuine
    /// credentials/registration problem. Only a 5xx (nothing behind the front door) or no answer at all is Down.
    /// </summary>
    private static SourceAvailabilityResult ClassifyStatus(int statusCode)
    {
        if (statusCode >= 500)
        {
            return SourceAvailabilityResult.Down($"The FHIR endpoint returned HTTP {statusCode}.");
        }

        return SourceAvailabilityResult.Up($"The FHIR endpoint answered with HTTP {statusCode}.");
    }

    private static bool IsOpenCircuit(Exception exception)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name.Contains("BrokenCircuit", StringComparison.Ordinal) ||
                type.Name.Contains("IsolatedCircuit", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Host and port only — never the path, which can carry tenant/practice identifiers.</summary>
    private static string DescribeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "the FHIR endpoint";

    /// <summary>
    /// Keyed on scheme/host/port so every connection pointed at the same instance shares one verdict. Hashed
    /// because a host name can identify a customer's organization and cache keys are visible in a shared Redis.
    /// </summary>
    private static string BuildCacheKey(Uri uri)
    {
        var authority = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(authority));
        return $"fhir-availability:{Convert.ToHexString(hash)[..32]}";
    }

    /// <summary>
    /// Cache reads and writes are best-effort. An unavailable cache (e.g. Redis down) must not fail a workflow run
    /// through a component whose entire purpose is to make failures clearer.
    /// </summary>
    private async Task<SourceAvailabilityResult?> TryReadCachedAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (_cache is null)
        {
            return null;
        }

        try
        {
            var payload = await _cache.GetStringAsync(cacheKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            var separator = payload.IndexOf('|', StringComparison.Ordinal);
            if (separator <= 0 ||
                !Enum.TryParse<SourceAvailability>(payload[..separator], out var availability))
            {
                return null;
            }

            return new SourceAvailabilityResult(availability, payload[(separator + 1)..]);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not read the cached source-availability verdict.");
            return null;
        }
    }

    private async Task TryWriteCachedAsync(
        string cacheKey,
        SourceAvailabilityResult result,
        CancellationToken cancellationToken)
    {
        if (_cache is null)
        {
            return;
        }

        try
        {
            await _cache.SetStringAsync(
                cacheKey,
                $"{result.Availability}|{result.Detail}",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = VerdictCacheLifetime },
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not cache the source-availability verdict.");
        }
    }
}
