using System.Text.Json;
using FHIRBridge.Api.Cors;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers;

/// <summary>
/// The public HL7 CDS Hooks 2.0 endpoints EHRs call: discovery, service invocation and card feedback. They sit
/// outside /api/v1 at the spec's fixed paths. Discovery is anonymous; invoke and feedback carry an EHR-signed JWT
/// that ICdsHooksService validates against the registered trusted issuers, so the endpoints are
/// [AllowAnonymous] to ASP.NET Core (see CdsHooksAuthenticationHandler) and CSRF-exempt (no portal cookie is
/// involved; the CORS policy allows no credentials). Request bodies hold PHI and are never logged.
/// </summary>
/// <remarks>
/// [ApiController], [FromBody] and [Consumes] are deliberately not used: their automatic 400/415 answers would
/// bypass the service, leaving no CDS call log row. The body is read here; one that cannot be read reaches the
/// service as null and is answered 400 ("not valid JSON") and recorded there, after the service id and JWT are
/// checked.
/// </remarks>
[AllowAnonymous]
[CsrfExempt]
[EnableCors(DynamicPortalCorsPolicyProvider.CdsHooksPolicyName)]
[EnableRateLimiting("cds")]
[Route(FhirBridgeAuthenticationExtensions.CdsHooksPathPrefix)]
[Produces("application/json")]
public sealed class CdsHooksController : ControllerBase
{
    /// <summary>A CDS request carries context plus prefetch bundles; 4 MB leaves room for long medication lists.</summary>
    private const long MaxInvokeBodyBytes = 4 * 1024 * 1024;

    private const long MaxFeedbackBodyBytes = 256 * 1024;

    /// <summary>Web defaults: case-insensitive names and numbers sent as strings, so a lenient client still binds.</summary>
    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ICdsHooksService _service;

    public CdsHooksController(ICdsHooksService service)
    {
        _service = service;
    }

    /// <summary>CDS Hooks discovery: every enabled service with its hook and prefetch templates.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CdsDiscoveryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Discovery(CancellationToken cancellationToken) =>
        Ok(await _service.GetDiscoveryAsync(cancellationToken));

    /// <summary>Invokes one service. 200 with cards (possibly none), or 400/401/403/404/412/500.</summary>
    [HttpPost("{serviceId}")]
    [RequestSizeLimit(MaxInvokeBodyBytes)]
    [ProducesResponseType(typeof(CdsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Invoke(string serviceId, CancellationToken cancellationToken)
    {
        var request = await ReadBodyAsync<CdsRequest>(cancellationToken);
        var result = await _service.InvokeAsync(serviceId, request, Caller(), cancellationToken);
        return result.Status == CdsCallStatus.Ok
            ? Ok(result.Response ?? new CdsResponse())
            : Failure(result.Status, result.Error);
    }

    /// <summary>Card feedback (CDS Hooks 2.0): accepted/overridden outcomes. Recorded as counts only.</summary>
    [HttpPost("{serviceId}/feedback")]
    [RequestSizeLimit(MaxFeedbackBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Feedback(string serviceId, CancellationToken cancellationToken)
    {
        var request = await ReadBodyAsync<CdsFeedbackRequest>(cancellationToken);
        var result = await _service.SubmitFeedbackAsync(serviceId, request, Caller(), cancellationToken);
        return result.Status == CdsCallStatus.Ok ? Ok() : Failure(result.Status, result.Error);
    }

    /// <summary>
    /// The body as JSON whatever Content-Type was sent, or null when it is empty, not JSON, or too large. Read here
    /// rather than by [FromBody] so MVC's 415 and model validation never answer before the service, which must see
    /// (and record) every call. The exception is dropped unlogged: its message can quote request content.
    /// </summary>
    private async Task<T?> ReadBodyAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(Request.Body, BodyJsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or BadHttpRequestException)
        {
            return null;
        }
    }

    private CdsCallerContext Caller() =>
        new(Request.Headers.Authorization.ToString() is { Length: > 0 } header ? header : null,
            $"{Request.Scheme}://{Request.Host}{Request.PathBase}");

    private ObjectResult Failure(CdsCallStatus status, string? error)
    {
        if (status == CdsCallStatus.Unauthorized)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
        }

        return StatusCode((int)status, new { error = error ?? "The request could not be completed." });
    }
}
