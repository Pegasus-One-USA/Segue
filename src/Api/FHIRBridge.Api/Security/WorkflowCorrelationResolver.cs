using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Resolves the shared workflow correlation id for an inbound request from the path and query string alone,
/// early enough in the pipeline to stamp it before anything reads it — see <see cref="WorkflowCorrelationId"/>
/// for why the id is derived rather than demanded from the caller.
/// <para>Runs before routing, so route values are not yet populated: the workflow id is matched off the raw path
/// instead. Endpoints whose session id is NOT on the query string (<c>POST /run</c>, which carries it in the body,
/// and <c>/oauth/callback</c>, which carries it inside the encrypted state) stamp themselves from their own
/// already-parsed values — see <see cref="ApplyDerived"/>.</para>
/// </summary>
public static partial class WorkflowCorrelationResolver
{
    /// <summary>Matches the workflow id in <c>/api/v1/workflows/{guid}/…</c>. Deliberately does NOT match
    /// <c>/api/v1/workflows/runs/{guid}/…</c> (a run id, not a workflow id) — "runs" is not a guid, so the
    /// pattern rejects it naturally.</summary>
    [GeneratedRegex(
        @"/workflows/(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(/|$)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowIdInPath();

    /// <summary>
    /// The derived correlation id for this request, or null when it cannot be derived (not a workflow-scoped
    /// path, or no session id supplied). A null result means "leave the ambient correlation id alone".
    /// </summary>
    public static string? Resolve(HttpContext context)
    {
        // An explicit header is the caller stating what to correlate under; never second-guess it.
        if (!string.IsNullOrWhiteSpace(context.Request.Headers["X-Correlation-Id"].FirstOrDefault()))
        {
            return null;
        }

        var match = WorkflowIdInPath().Match(context.Request.Path.Value ?? string.Empty);
        if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out var workflowId))
        {
            return null;
        }

        return WorkflowCorrelationId.Derive(workflowId, ResolveSessionId(context.Request.Query));
    }

    /// <summary>
    /// Stamps <paramref name="correlationId"/> onto the request so every downstream consumer picks it up —
    /// <c>HttpContextCurrentUserService</c> (and therefore every governance table write), the outbound
    /// <c>ApiRequestLoggingHandler</c>, and the global exception handler all resolve
    /// <c>TraceIdentifier</c> when no header was supplied.
    /// <para>For endpoints that only learn their session id after model binding or after decrypting an OAuth
    /// state, call this from the handler itself — the header check in <see cref="Resolve"/> still applies, so a
    /// caller-supplied correlation id is never overwritten.</para>
    /// </summary>
    public static void ApplyDerived(HttpContext? context, Guid workflowId, string? sessionId)
    {
        if (context is null
            || !string.IsNullOrWhiteSpace(context.Request.Headers["X-Correlation-Id"].FirstOrDefault()))
        {
            return;
        }

        var derived = WorkflowCorrelationId.Derive(workflowId, sessionId);
        if (derived is not null)
        {
            context.TraceIdentifier = derived;
        }
    }

    /// <summary>
    /// Stamps an already-minted, attempt-scoped correlation id (see <c>validate-run</c>) onto the request. Unlike
    /// <see cref="ApplyDerived"/> there is nothing to compute — this id IS the attempt's identity, so it is applied
    /// as given. A caller-supplied <c>X-Correlation-Id</c> still wins, since that is the caller stating explicitly
    /// what to correlate under.
    /// </summary>
    public static void ApplyExplicit(HttpContext? context, string correlationId)
    {
        if (context is null
            || string.IsNullOrWhiteSpace(correlationId)
            || !string.IsNullOrWhiteSpace(context.Request.Headers["X-Correlation-Id"].FirstOrDefault()))
        {
            return;
        }

        context.TraceIdentifier = correlationId;
    }

    /// <summary>
    /// sessionId wins over callerId wherever both are present. They mean different things on the mint endpoints —
    /// there, callerId is the app's return URL and sessionId is the opaque token-cache key — while on
    /// token-status/discard-token the cache key arrives as callerId. Preferring sessionId keeps all of them
    /// resolving to the same value for one browser session.
    /// </summary>
    private static string? ResolveSessionId(IQueryCollection query)
    {
        var sessionId = query["sessionId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId;
        }

        var callerId = query["callerId"].FirstOrDefault();

        // A callerId that is a URL is a return address (the mint endpoints' meaning), never a cache key —
        // deriving from it would give the OAuth legs a different id than every other call in the same attempt.
        return Uri.IsWellFormedUriString(callerId, UriKind.Absolute) ? null : callerId;
    }
}
