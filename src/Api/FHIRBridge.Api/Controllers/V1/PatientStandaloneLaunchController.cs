using FHIRBridge.Api.Security;
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
    private readonly IConfiguration _configuration;

    public PatientStandaloneLaunchController(
        IInteractiveSourceAuthorizationService authorizationService,
        IWorkflowDefinitionStore workflowDefinitionStore,
        IPatientStandaloneEhrEndpointService myChartEndpointService,
        IConfiguration configuration)
    {
        _authorizationService = authorizationService;
        _workflowDefinitionStore = workflowDefinitionStore;
        _myChartEndpointService = myChartEndpointService;
        _configuration = configuration;
    }

    [HttpGet("workflows/{workflowId:guid}/public-patient-standalone-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicPatientStandaloneUrl(
        Guid workflowId, [FromQuery] Guid ehrEndpointId, [FromQuery] string? callerId, CancellationToken cancellationToken)
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

        // Anonymous endpoint — validate callerId against Portal:AllowedOrigins before honoring it (see
        // CallerIdOriginValidator), so it can't be used as an open redirect off a real MyChart login.
        if (!string.IsNullOrWhiteSpace(callerId) && !CallerIdOriginValidator.IsAllowedOrigin(callerId, _configuration))
        {
            return BadRequest(new { error = "invalid_request", error_description = "callerId is not an allowed origin." });
        }

        var context = _authorizationService.BuildWorkflowLaunchContextToken(workflowId, ehrEndpointId, callerId);
        return Ok(BuildLaunchResponse(context));
    }

    // This controller only ever reaches here once applicationType has already been confirmed as Patient above, so
    // unlike OAuthController's BuildLaunchResponse, there is no other mode/opensDirectly branch to consider — the
    // shape returned still matches it (launchUrl, mode, opensDirectly, applicationType) for the frontend's benefit.
    private object BuildLaunchResponse(string context) => new
    {
        launchUrl = BuildAuthorizeUri(context),
        mode = "patient",
        opensDirectly = true,
        applicationType = ApplicationType.Patient.ToString(),
    };

    private string BuildAuthorizeUri(string context) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/v1/oauth/authorize/{context}";
}
