using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Services;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.SharedKernel.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Anonymous "mint a public launch URL" endpoint for the Patient Standalone flow — the MyChart-endpoint counterpart
/// of <see cref="OAuthController.GetPublicWorkflowStandaloneUrl"/>. Deliberately a separate controller (not an added
/// branch on OAuthController) so the existing Provider Standalone endpoint's Epic-only gate never has to change to
/// support this; the two flows are fully independent from here down. Only mints a context for a workflow the admin
/// has explicitly opted in via <c>POST /workflows/{workflowId}/enable-public-launch</c> (the same, unmodified
/// mechanism Provider Standalone already uses) whose source resolves to <see cref="ApplicationType.Patient"/>, and
/// only for an <paramref name="ehrEndpointId"/> that resolves to an EndpointType.MyChart row — never the shared Epic
/// sandbox, which stays behind the Provider Standalone endpoint.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("oauth")]
[Route("api/v1")]
public sealed class PatientStandaloneLaunchController : ControllerBase
{
    private readonly IInteractiveSourceAuthorizationService _authorizationService;
    private readonly IWorkflowDefinitionStore _workflowDefinitionStore;
    private readonly IPatientStandaloneEhrEndpointService _myChartEndpointService;
    private readonly IAllowedCorsOriginsCache _allowedCorsOriginsCache;

    public PatientStandaloneLaunchController(
        IInteractiveSourceAuthorizationService authorizationService,
        IWorkflowDefinitionStore workflowDefinitionStore,
        IPatientStandaloneEhrEndpointService myChartEndpointService,
        IAllowedCorsOriginsCache allowedCorsOriginsCache)
    {
        _authorizationService = authorizationService;
        _workflowDefinitionStore = workflowDefinitionStore;
        _myChartEndpointService = myChartEndpointService;
        _allowedCorsOriginsCache = allowedCorsOriginsCache;
    }

    [HttpGet("workflows/{workflowId:guid}/public-patient-standalone-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicPatientStandaloneUrl(
        Guid workflowId, [FromQuery] Guid ehrEndpointId, [FromQuery] string? callerId, [FromQuery] string? sessionId,
        CancellationToken cancellationToken)
    {
        var workflow = await _workflowDefinitionStore.GetAsync(workflowId, cancellationToken);
        if (workflow is null || !workflow.IsPubliclyLaunchable)
        {
            return NotFound();
        }

        if (!await _myChartEndpointService.IsMyChartEndpointAsync(ehrEndpointId, cancellationToken))
        {
            return NotFound();
        }

        var applicationType = await _authorizationService.GetWorkflowApplicationTypeAsync(workflowId, cancellationToken);
        if (applicationType is not ApplicationType.Patient)
        {
            return NotFound();
        }

        // Anonymous endpoint — validate callerId against the live allowed-origins set before honoring it (see
        // CallerIdOriginValidator), so it can't be used as an open redirect off a real MyChart login.
        if (!string.IsNullOrWhiteSpace(callerId)
            && !await CallerIdOriginValidator.IsAllowedOriginAsync(callerId, _allowedCorsOriginsCache, cancellationToken))
        {
            return BadRequest(new { error = "invalid_request", error_description = "callerId is not an allowed origin." });
        }

        // sessionId is an opaque identifier (never a URL, unlike callerId, so no origin check applies) that the
        // Patient Standalone interactive token cache keys on instead of SourceConnectionId — see
        // SmartAuthorizationCodeTokenProvider.BuildStoreKey. Reuse whatever the caller already has (a returning
        // browser session resuming after a token expired) rather than always minting fresh, so its later
        // hasValidToken/run calls keep finding the same cached token. Mint one here (not left to the caller) when
        // absent so a first-time visitor still gets a value to persist and echo back on every later call. Capped
        // defensively — this rides inside an encrypted token but a client could still send an unreasonably large
        // string.
        var effectiveSessionId = !string.IsNullOrWhiteSpace(sessionId) && sessionId.Length <= 200
            ? sessionId
            : Guid.NewGuid().ToString("N");

        var context = _authorizationService.BuildWorkflowLaunchContextToken(workflowId, ehrEndpointId, callerId, effectiveSessionId);
        return Ok(BuildLaunchResponse(context, effectiveSessionId));
    }

    // This controller only ever reaches here once applicationType has already been confirmed as Patient above, so
    // unlike OAuthController's BuildLaunchResponse, there is no other mode/opensDirectly branch to consider — the
    // shape returned still matches it (launchUrl, mode, opensDirectly, applicationType) for the frontend's benefit,
    // plus sessionId so a first-time caller can persist and echo it on every later hasValidToken/run/discardToken
    // call for this same browser session.
    private object BuildLaunchResponse(string context, string sessionId) => new
    {
        launchUrl = BuildAuthorizeUri(context),
        mode = "patient",
        opensDirectly = true,
        applicationType = ApplicationType.Patient.ToString(),
        sessionId,
    };

    private string BuildAuthorizeUri(string context) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/oauth/authorize/{context}";
}
