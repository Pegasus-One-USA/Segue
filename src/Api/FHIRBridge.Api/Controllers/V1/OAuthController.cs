using FHIRBridge.Application.Abstractions.Sources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// The interactive SMART-on-FHIR OAuth round-trip for source connections (EHR launch / provider standalone /
/// patient standalone). An admin hits <c>authorize</c> to start the sign-in; the EHR then redirects the browser
/// back to <c>callback</c>, which exchanges the code for a token that later pipeline runs read back.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class OAuthController : ControllerBase
{
    private readonly IInteractiveSourceAuthorizationService _authorizationService;

    public OAuthController(IInteractiveSourceAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// Starts an interactive sign-in for a source connection and redirects the browser to the EHR's authorization
    /// endpoint. Admin-only; the source's SMART endpoints are discovered on demand.
    /// </summary>
    [Authorize]
    [HttpGet("tenants/{tenantId:guid}/source-connections/{sourceConnectionId:guid}/oauth/authorize")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Authorize(Guid tenantId, Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var authorizationUrl = await _authorizationService.StartAsync(
            tenantId, sourceConnectionId, BuildCallbackUri(), cancellationToken);

        return Redirect(authorizationUrl.ToString());
    }

    /// <summary>
    /// The SMART EHR-launch entry point registered with the EHR. The EHR redirects the user's browser here with the
    /// issuer (<c>iss</c>) and opaque <c>launch</c> token; the issuer is validated against the source's trusted-issuer
    /// allow-list, then the browser is redirected on to the authorization endpoint. Anonymous — the launching user has
    /// no FHIRBridge session; security comes from the trusted-issuer check.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("tenants/{tenantId:guid}/source-connections/{sourceConnectionId:guid}/oauth/launch")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Launch(
        Guid tenantId,
        Guid sourceConnectionId,
        [FromQuery] string? iss,
        [FromQuery] string? launch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(launch))
        {
            return BadRequest(new { error = "invalid_request", error_description = "Missing iss or launch." });
        }

        var authorizationUrl = await _authorizationService.StartEhrLaunchAsync(
            tenantId, sourceConnectionId, iss, launch, BuildCallbackUri(), cancellationToken);

        return Redirect(authorizationUrl.ToString());
    }

    /// <summary>
    /// The OAuth redirect target registered with the EHR. Anonymous — it is secured by the single-use, unguessable
    /// <c>state</c> value rather than the caller's session. Completes the sign-in and persists the token.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("oauth/callback")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return BadRequest(new { error, error_description = errorDescription });
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return BadRequest(new { error = "invalid_request", error_description = "Missing authorization code or state." });
        }

        var result = await _authorizationService.CompleteAsync(state, code, cancellationToken);

        return Ok(new
        {
            message = "Authorization complete. You can close this window and return to FHIRBridge.",
            tenantId = result.TenantId,
            sourceConnectionId = result.SourceConnectionId,
            source = result.SourceName
        });
    }

    // The absolute callback URL registered with the EHR. Built from the incoming request so it matches the host the
    // admin is on; a multi-host deployment would instead resolve this from configuration.
    private string BuildCallbackUri() =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/oauth/callback";
}
