using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// The public CDS Hooks 2.0 endpoints (GET /cds-services, POST /cds-services/{id}, POST
/// /cds-services/{id}/feedback). Validates the caller's JWT against the registered trusted issuers, runs the
/// instance's handler, and writes one CDS call log row per call (metadata only — never request content).
/// </summary>
public interface ICdsHooksService
{
    /// <summary>Every enabled instance, with its handler's prefetch templates for its hook.</summary>
    Task<CdsDiscoveryResponse> GetDiscoveryAsync(CancellationToken cancellationToken);

    /// <param name="request">The parsed body, or null when it was missing or not valid JSON (answered 400 once the caller is authenticated).</param>
    Task<CdsInvokeResult> InvokeAsync(
        string serviceId, CdsRequest? request, CdsCallerContext caller, CancellationToken cancellationToken);

    /// <param name="request">The parsed body, or null when it was missing or not valid JSON.</param>
    Task<CdsFeedbackResult> SubmitFeedbackAsync(
        string serviceId, CdsFeedbackRequest? request, CdsCallerContext caller, CancellationToken cancellationToken);

    /// <summary>
    /// Records an invoke/feedback call the license gate refused (503) before it reached the service, so failing
    /// EHR integrations still show in Activity. Writes the call log row and metric only.
    /// </summary>
    Task RecordUnlicensedAsync(string serviceId, bool feedback);
}

/// <param name="AuthorizationHeader">The raw Authorization header ("Bearer &lt;jwt&gt;"), or null. Never logged.</param>
/// <param name="RequestOrigin">
/// "{scheme}://{host}" of the incoming request. Never used for the expected <c>aud</c>: that comes only from the
/// configured Cds:PublicBaseUrl (see <see cref="ICdsPublicBaseUrlResolver"/>), so a Host header cannot steer it.
/// </param>
public sealed record CdsCallerContext(string? AuthorizationHeader, string RequestOrigin);

/// <summary>The HTTP-level result of a CDS call; the controller maps <see cref="Status"/> to a status code.</summary>
public enum CdsCallStatus
{
    Ok = 200,
    BadRequest = 400,
    Unauthorized = 401,
    Forbidden = 403,
    NotFound = 404,
    PreconditionFailed = 412,

    /// <summary>The client hung up before the call finished (nginx's 499). Recorded on the call log only.</summary>
    ClientClosedRequest = 499,
    Error = 500,

    /// <summary>Refused before evaluation because Segue has no active license (see the license gate in Program.cs).</summary>
    ServiceUnavailable = 503,
}

/// <param name="Response">The cards, set when <paramref name="Status"/> is Ok.</param>
/// <param name="Error">A short, client-safe, non-PHI message for a non-Ok status.</param>
public sealed record CdsInvokeResult(CdsCallStatus Status, CdsResponse? Response, string? Error = null);

public sealed record CdsFeedbackResult(CdsCallStatus Status, string? Error = null);
