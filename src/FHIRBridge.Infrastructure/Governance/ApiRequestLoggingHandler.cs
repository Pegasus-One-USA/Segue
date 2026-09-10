using System.Diagnostics;
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

        try
        {
            response = await base.SendAsync(request, cancellationToken);
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
                        urlWithoutQuery,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        error,
                        correlationId),
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
}
