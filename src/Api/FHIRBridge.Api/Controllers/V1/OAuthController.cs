using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.SharedKernel.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

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
    [HttpGet("source-connections/{sourceConnectionId:guid}/oauth/authorize")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Authorize(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var authorizationUrl = await _authorizationService.StartAsync(
            sourceConnectionId, BuildCallbackUri(), cancellationToken);

        return Redirect(authorizationUrl.ToString());
    }

    /// <summary>
    /// The SMART EHR-launch entry point registered with the EHR. The EHR redirects the user's browser here with the
    /// issuer (<c>iss</c>) and opaque <c>launch</c> token; the issuer is validated against the source's trusted-issuer
    /// allow-list, then the browser is redirected on to the authorization endpoint. Anonymous — the launching user has
    /// no FHIRBridge session; security comes from the trusted-issuer check.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("oauth")]
    [HttpGet("source-connections/{sourceConnectionId:guid}/oauth/launch")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Launch(
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
            sourceConnectionId, iss, launch, BuildCallbackUri(), cancellationToken);

        return Redirect(authorizationUrl.ToString());
    }

    /// <summary>
    /// Returns the opaque, encrypted launch URL to register with the EHR for a specific pipeline route. The route id
    /// is encrypted into the URL, so raw GUIDs are never exposed. Admin-only.
    /// </summary>
    [Authorize]
    [HttpGet("pipelines/{routeId:guid}/launch-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLaunchUrl(Guid routeId, [FromQuery] Guid? ehrEndpointId, CancellationToken cancellationToken)
    {
        var applicationType = await _authorizationService.GetRouteApplicationTypeAsync(routeId, cancellationToken);
        var context = _authorizationService.BuildLaunchContextToken(routeId, ehrEndpointId);
        return Ok(BuildLaunchResponse(applicationType, context));
    }

    /// <summary>
    /// Returns the opaque, encrypted launch URL for a workflow graph. On launch, the workflow's source node's
    /// connection drives OAuth + trusted-issuer validation; on callback the workflow is run. Admin-only.
    /// <paramref name="ehrEndpointId"/> optionally names a specific hospital/organization EhrEndpoint (from the
    /// EhrEndpoints directory) to launch against instead of the source connection's own configured base URL — set
    /// this when a third-party app carries a user's hospital selection through the launch.
    /// </summary>
    [Authorize]
    [HttpGet("workflows/{workflowId:guid}/launch-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkflowLaunchUrl(Guid workflowId, [FromQuery] Guid? ehrEndpointId, CancellationToken cancellationToken)
    {
        var applicationType = await _authorizationService.GetWorkflowApplicationTypeAsync(workflowId, cancellationToken);
        var context = _authorizationService.BuildWorkflowLaunchContextToken(workflowId, ehrEndpointId);
        return Ok(BuildLaunchResponse(applicationType, context));
    }

    /// <summary>
    /// The SMART EHR-launch entry point registered with the EHR for a specific pipeline route. The route is
    /// carried in the encrypted <paramref name="context"/> segment — no raw GUIDs in the URL. The EHR appends the
    /// issuer (<c>iss</c>) + opaque <c>launch</c> token; on callback the resolved route is run for the launched
    /// patient. Anonymous — the launching user has no FHIRBridge session; security comes from the trusted-issuer check.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("oauth")]
    [HttpGet("oauth/launch/{context}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LaunchPipeline(
        string context,
        [FromQuery] string? iss,
        [FromQuery] string? launch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(launch))
        {
            return BadRequest(new { error = "invalid_request", error_description = "Missing iss or launch." });
        }

        var authorizationUrl = await _authorizationService.StartEhrLaunchFromContextAsync(
            context, iss, launch, BuildCallbackUri(), cancellationToken);

        return Redirect(authorizationUrl.ToString());
    }

    /// <summary>
    /// Returns the opaque, encrypted provider-standalone URL to hand to a provider for a specific pipeline route.
    /// Clicking it takes the provider straight to the EHR's login (no <c>iss</c>/<c>launch</c> handshake); after
    /// sign-in and patient selection the callback runs the route for the selected patient. Admin-only.
    /// <paramref name="ehrEndpointId"/> optionally names a specific hospital/organization EhrEndpoint to launch
    /// against instead of the source connection's own configured base URL — set this when a third-party app carries
    /// a user's hospital selection through the launch.
    /// </summary>
    [Authorize]
    [HttpGet("pipelines/{routeId:guid}/standalone-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStandaloneUrl(Guid routeId, [FromQuery] Guid? ehrEndpointId)
    {
        var context = _authorizationService.BuildLaunchContextToken(routeId, ehrEndpointId);
        return Ok(new { standaloneUrl = BuildStandaloneUri(context) });
    }

    /// <summary>
    /// The shareable provider-standalone entry point for a specific pipeline route. The route is carried in the
    /// encrypted <paramref name="context"/> segment — no raw GUIDs in the URL, and no <c>iss</c>/<c>launch</c>
    /// parameters: the browser is redirected straight to the source's authorization endpoint, where the provider
    /// signs in and picks a patient. Anonymous — the provider has no FHIRBridge session; security comes from the
    /// tamper-proof encrypted context.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("oauth")]
    [HttpGet("oauth/standalone/{context}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> StandaloneLaunch(string context, CancellationToken cancellationToken)
    {
        var authorizationUrl = await _authorizationService.StartStandaloneFromContextAsync(
            context, BuildCallbackUri(), cancellationToken);

        return Redirect(authorizationUrl.ToString());
    }

    /// <summary>
    /// The directly-opened entry point for provider-standalone / patient workflows: no EHR <c>iss</c>/<c>launch</c> is
    /// needed (a clinician or patient opens this link themselves). The workflow/route is carried in the encrypted
    /// <paramref name="context"/>; this starts the authorization-code + PKCE flow and, on callback, runs it. Anonymous —
    /// the launching user has no FHIRBridge session; security comes from PKCE + the single-use OAuth state.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("oauth")]
    [HttpGet("oauth/authorize/{context}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AuthorizeFromContext(string context, CancellationToken cancellationToken)
    {
        var authorizationUrl = await _authorizationService.StartInteractiveFromContextAsync(
            context, BuildCallbackUri(), cancellationToken);

        return Redirect(authorizationUrl.ToString());
    }

    /// <summary>
    /// The OAuth redirect target registered with the EHR. Anonymous — it is secured by the single-use, unguessable
    /// <c>state</c> value rather than the caller's session. Completes the sign-in and persists the token.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("oauth")]
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

        // A workflow-triggered launch with a configured PostLaunchRedirectUri hands the browser back to the
        // third-party app that opened the EHR launch, rather than leaving it on this bare JSON response — the run
        // id lets that app fetch its result (see GetWorkflowRunLaunchResult on WorkflowEndpoints).
        if (result.WorkflowRunId is { } workflowRunId && !string.IsNullOrWhiteSpace(result.PostLaunchRedirectUri))
        {
            var returnUrl = QueryHelpers.AddQueryString(
                result.PostLaunchRedirectUri, "workflowRunId", workflowRunId.ToString());
            return Redirect(returnUrl);
        }

        return Ok(new
        {
            message = "Authorization complete. You can close this window and return to FHIRBridge.",
            sourceConnectionId = result.SourceConnectionId,
            source = result.SourceName
        });
    }

    // The absolute callback URL registered with the EHR. Built from the incoming request so it matches the host the
    // admin is on; a multi-host deployment would instead resolve this from configuration.
    private string BuildCallbackUri() =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/oauth/callback";

    private string BuildLaunchUri(string context) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/oauth/launch/{context}";

    private string BuildAuthorizeUri(string context) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/oauth/authorize/{context}";

    private string BuildStandaloneUri(string context) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/oauth/standalone/{context}";

    /// <summary>
    /// Shapes the launch-URL response by application type. EHR-launch sources get the <c>/oauth/launch</c> entry (the
    /// EHR appends iss + launch and invokes it — it is not opened directly); standalone / patient sources get the
    /// directly-openable <c>/oauth/authorize</c> entry. <c>opensDirectly</c> + <c>mode</c> let the portal label it.
    /// </summary>
    private object BuildLaunchResponse(ApplicationType? applicationType, string context)
    {
        var opensDirectly = applicationType is ApplicationType.Standalone or ApplicationType.Patient;
        var mode = "ehr-launch";
        if (applicationType is ApplicationType.Standalone) mode = "standalone";
        else if (applicationType is ApplicationType.Patient) mode = "patient";
        return new
        {
            launchUrl = opensDirectly ? BuildAuthorizeUri(context) : BuildLaunchUri(context),
            mode,
            opensDirectly,
            applicationType = applicationType?.ToString(),
        };
    }
}
