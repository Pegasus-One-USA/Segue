using System.Diagnostics;
using System.Linq;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Wraps every outbound HttpClient call (source/destination/terminology connectors) registered via
/// <c>ConfigureHttpClientDefaults</c>. Records method/URL/status/duration only — the query string is
/// stripped (some upstream APIs put tokens there; search params can carry PHI-ish identifiers) and
/// request/response bodies or headers are never captured. Creates its own DI scope per call rather than
/// depending on ambient scope semantics of the message-handler pipeline, since handler instances can
/// outlive any single request.
/// </summary>
public sealed class ApiRequestLoggingHandler : DelegatingHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApiMetrics _apiMetrics;

    public ApiRequestLoggingHandler(IServiceScopeFactory scopeFactory, IApiMetrics apiMetrics)
    {
        _scopeFactory = scopeFactory;
        _apiMetrics = apiMetrics;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        string? error = null;
        var capture = request.Options.TryGetValue(ApiCallCaptureOptions.Capture, out var captureFlag) && captureFlag;

        try
        {
            response = await base.SendAsync(request, cancellationToken);

            // Temporary troubleshooting capture only — see ApiCallCaptureOptions' own doc comment. Read and
            // stashed back onto the SAME request's Options here (not just handed to the governance write below)
            // so the caller — which still holds this exact HttpRequestMessage after SendAsync returns — can read
            // its own response detail back out without a second content read.
            if (capture)
            {
                var responseBody = await SafeReadResponseBodyAsync(response, cancellationToken);
                var responseHeaders = FormatHeaders(response.Headers, response.Content.Headers);
                request.Options.Set(ApiCallCaptureOptions.ResponseBody, responseBody);
                request.Options.Set(ApiCallCaptureOptions.ResponseHeaders, responseHeaders);
            }

            return response;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            throw;
        }
        finally
        {
            stopwatch.Stop();

            using var scope = _scopeFactory.CreateScope();
            var governanceLogger = scope.ServiceProvider.GetRequiredService<IGovernanceLogger>();

            // Nullable: the Worker host has no ICurrentUserService registration at all when running
            // against the in-memory (no-database) configuration — correlation id is simply omitted then.
            var correlationId = scope.ServiceProvider.GetService<ICurrentUserService>()?.CurrentUser.CorrelationId;

            var uri = request.RequestUri;
            var urlWithoutQuery = uri is null ? "" : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
            var statusCode = (int?)response?.StatusCode;

            // Temporary troubleshooting capture only (see ApiCallCaptureOptions) — the caller already computed
            // its own masked URL/headers/body before sending (it alone knows which of its own headers/query
            // params carry a credential), so this handler never re-derives or re-masks anything itself.
            var maskedUrl = capture && request.Options.TryGetValue(ApiCallCaptureOptions.MaskedUrl, out var mu)
                ? mu : null;
            var requestHeaders = capture && request.Options.TryGetValue(ApiCallCaptureOptions.RequestHeaders, out var rh)
                ? rh : null;
            var requestBody = capture && request.Options.TryGetValue(ApiCallCaptureOptions.RequestBody, out var rb)
                ? rb : null;
            var responseHeadersCaptured = capture && request.Options.TryGetValue(ApiCallCaptureOptions.ResponseHeaders, out var rsh)
                ? rsh : null;
            var responseBodyCaptured = capture && request.Options.TryGetValue(ApiCallCaptureOptions.ResponseBody, out var rsb)
                ? rsb : null;

            // Best-effort, and genuinely so: this runs in a finally, where an exception REPLACES whatever the
            // outbound call was about to return (or throw). Without this catch a governance-write failure — a
            // schema drift, a transient DB outage, a full disk — silently converts every successful upstream call
            // into a failure, taking the whole pipeline down for a reason that has nothing to do with the call
            // itself. Logging is never worth that. The fresh cancellation token below is for the same reason: a
            // cancelled request should still get logged.
            try
            {
                await governanceLogger.LogApiRequestAsync(
                    new ApiRequestEntry(
                        request.Method.Method,
                        maskedUrl ?? urlWithoutQuery,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        error,
                        correlationId,
                        RequestHeaders: requestHeaders,
                        RequestBody: requestBody,
                        ResponseHeaders: responseHeadersCaptured,
                        ResponseBody: responseBodyCaptured),
                    CancellationToken.None);
            }
            catch
            {
                // Deliberately swallowed — see above. Nothing is rethrown, and nothing is logged through a second
                // channel that could fail the same way.
            }

            _apiMetrics.RecordRequest(new ApiRequestMetric(
                request.Method.Method, urlWithoutQuery, statusCode, stopwatch.ElapsedMilliseconds));
        }
    }

    /// <summary>Temporary troubleshooting capture only — see ApiCallCaptureOptions. A response body that fails to
    /// read (already-disposed stream, network drop mid-read) must not blow up the outbound call over a log/audit
    /// write.</summary>
    private static async Task<string> SafeReadResponseBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return $"<could not read response body: {exception.Message}>";
        }
    }

    /// <summary>Temporary troubleshooting capture only — see ApiCallCaptureOptions. No masking here: the caller's
    /// own request-side headers are pre-masked before this handler ever sees them, and a response from the
    /// destination server is not expected to echo our credential back.</summary>
    private static string FormatHeaders(System.Net.Http.Headers.HttpHeaders headers, System.Net.Http.Headers.HttpContentHeaders contentHeaders)
    {
        var lines = headers.Concat(contentHeaders)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");
        var joined = string.Join(" | ", lines);
        return string.IsNullOrEmpty(joined) ? "(none)" : joined;
    }
}
