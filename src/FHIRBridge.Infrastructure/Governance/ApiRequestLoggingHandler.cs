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

            // Best-effort: the log write itself must never fail (or block) the outbound call's own
            // cancellation/disposal — use a fresh token so a cancelled request still gets logged.
            await governanceLogger.LogApiRequestAsync(
                new ApiRequestEntry(
                    request.Method.Method,
                    urlWithoutQuery,
                    statusCode,
                    stopwatch.ElapsedMilliseconds,
                    error,
                    correlationId),
                CancellationToken.None);

            _apiMetrics.RecordRequest(new ApiRequestMetric(
                request.Method.Method, urlWithoutQuery, statusCode, stopwatch.ElapsedMilliseconds));
        }
    }
}
