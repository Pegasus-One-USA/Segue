using System.Diagnostics;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Governance;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Records one <see cref="ApiRequestLog"/> row per inbound call to the workflow/launch API surface, mirroring
/// what <c>ApiRequestLoggingHandler</c> already does for outbound calls.
/// <para>Without this, a request this API <em>refuses</em> leaves no durable trace at all: it makes no outbound
/// call, throws no exception (a <c>404</c> from the public-launch gate is a normal result, not a fault), and
/// starts no run — so the only record is a Serilog line, and there is no Seq instance in production. That is
/// exactly the "the workflow was not publicly launchable and Execution History showed nothing" case.</para>
/// <para>Scoped deliberately narrowly (see <see cref="ShouldLog"/>): this is a diagnostic trail for workflow
/// attempts, not a general access log, and the portal's own polling would otherwise dominate the table.</para>
/// </summary>
public sealed class InboundApiRequestLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public InboundApiRequestLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IGovernanceLogger governanceLogger)
    {
        if (!ShouldLog(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        string? error = null;
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            throw;
        }
        finally
        {
            stopwatch.Stop();

            try
            {
                // Path only, never the query string — same rule the outbound handler follows, and for the same
                // reason: callerId/sessionId/userIdentity all ride on the query string here.
                await governanceLogger.LogApiRequestAsync(
                    new ApiRequestEntry(
                        context.Request.Method,
                        context.Request.Path.Value ?? "/",
                        context.Response.StatusCode,
                        stopwatch.ElapsedMilliseconds,
                        error,
                        CorrelationId: null,
                        Direction: ApiRequestDirection.Inbound),
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort, exactly like the outbound handler: failing to log must never turn a served request
                // into a failed one, nor mask an exception already propagating out of the pipeline above.
            }
        }
    }

    /// <summary>
    /// Only the endpoints that participate in a workflow attempt — the run/validate/token/launch surface plus the
    /// OAuth legs. Everything else (portal CRUD, static files, health checks, SignalR) is deliberately excluded:
    /// those are already covered by the audit trail where it matters, and including them would make this table
    /// grow without making a workflow attempt any easier to reconstruct.
    /// </summary>
    private static bool ShouldLog(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        var value = path.Value!;
        return value.Contains("/workflows/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/workflow-runs/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/oauth/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/ehr-public-endpoints", StringComparison.OrdinalIgnoreCase);
    }
}
