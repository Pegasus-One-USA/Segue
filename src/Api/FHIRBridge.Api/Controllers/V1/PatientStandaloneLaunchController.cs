using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.SharedKernel.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Anonymous "mint a public launch URL" endpoint for the Patient Standalone flow — the counterpart of
/// <see cref="OAuthController.GetPublicWorkflowStandaloneUrl"/>. Deliberately a separate controller (not an added
/// branch on OAuthController) so the two flows stay fully independent from here down. Only mints a context for a
/// workflow the admin has explicitly opted in via <c>POST /workflows/{workflowId}/enable-public-launch</c> (the
/// same, unmodified mechanism Provider Standalone already uses) whose source resolves to
/// <see cref="ApplicationType.Patient"/>, and only for an <paramref name="ehrEndpointId"/> that resolves to a known
/// EhrEndpoint row of type <see cref="EhrEndpointType.MyChart"/> — a real customer's own branded instance, never
/// the shared Epic sandbox.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("oauth")]
[Route("api/v1")]
public sealed class PatientStandaloneLaunchController : ControllerBase
{
    private readonly IInteractiveSourceAuthorizationService _authorizationService;
    private readonly IWorkflowDefinitionStore _workflowDefinitionStore;
    private readonly IEhrEndpointService _ehrEndpointService;
    private readonly IAllowedCorsOriginsCache _allowedCorsOriginsCache;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ILogger<PatientStandaloneLaunchController> _logger;

    public PatientStandaloneLaunchController(
        IInteractiveSourceAuthorizationService authorizationService,
        IWorkflowDefinitionStore workflowDefinitionStore,
        IEhrEndpointService ehrEndpointService,
        IAllowedCorsOriginsCache allowedCorsOriginsCache,
        IGovernanceLogger governanceLogger,
        ILogger<PatientStandaloneLaunchController> logger)
    {
        _authorizationService = authorizationService;
        _workflowDefinitionStore = workflowDefinitionStore;
        _ehrEndpointService = ehrEndpointService;
        _allowedCorsOriginsCache = allowedCorsOriginsCache;
        _governanceLogger = governanceLogger;
        _logger = logger;
    }

    /// <summary>Patient Standalone counterpart of <c>OAuthController.LogRefusedLaunchAsync</c> — see its remarks
    /// for why a pre-launch refusal needs a durable row of its own rather than only a 404.</summary>
    private async Task LogRefusedLaunchAsync(Guid workflowId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _governanceLogger.LogSmartLaunchAsync(
                new SmartLaunchEntry(
                    Guid.Empty, $"workflow:{workflowId}", "PatientStandalone", Success: false, FailureReason: reason),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not persist the refused-launch record for workflow {WorkflowId}.", workflowId);
        }
    }

    [HttpGet("workflows/{workflowId:guid}/public-patient-standalone-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicPatientStandaloneUrl(
        Guid workflowId, [FromQuery] Guid ehrEndpointId, [FromQuery] string? callerId, [FromQuery] string? sessionId,
        [FromQuery] string? userIdentity, CancellationToken cancellationToken)
    {
        var workflow = await _workflowDefinitionStore.GetAsync(workflowId, cancellationToken);
        if (workflow is null || !workflow.IsPubliclyLaunchable)
        {
            await LogRefusedLaunchAsync(
                workflowId,
                workflow is null
                    ? "Refused: no workflow with this id exists."
                    : "Refused: workflow is not opted into public launch (POST /workflows/{id}/enable-public-launch).",
                cancellationToken);
            return NotFound();
        }

        if (!await _ehrEndpointService.IsKnownEndpointAsync(ehrEndpointId, EhrEndpointType.MyChart, cancellationToken))
        {
            await LogRefusedLaunchAsync(
                workflowId,
                $"Refused: ehrEndpointId {ehrEndpointId} is not a known MyChart endpoint.",
                cancellationToken);
            return NotFound();
        }

        var applicationType = await _authorizationService.GetWorkflowApplicationTypeAsync(workflowId, cancellationToken);
        if (applicationType is not ApplicationType.Patient)
        {
            // The three refusals above and this one are all a bare 404 to the caller; only the reason recorded
            // here distinguishes "wrong application type" from "not opted in", which are fixed very differently.
            await LogRefusedLaunchAsync(
                workflowId,
                $"Refused: source ApplicationType is {applicationType}, but the Patient Standalone launch requires Patient.",
                cancellationToken);
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

        // See OAuthController.GetPublicWorkflowStandaloneUrl's matching call: on a first-ever visit no sessionId
        // was supplied, so the pipeline-level resolver had nothing to key on — re-stamp from the id minted above
        // so this mint shares a correlation id with the callback and the run it authorizes.
        WorkflowCorrelationResolver.ApplyDerived(HttpContext, workflowId, effectiveSessionId);

        // userIdentity is a stable identifier for HealthApp's own logged-in account (e.g. patient@healthapp.local)
        // — distinct from sessionId above, which is only an opaque per-browser cache key. When present, it is what
        // CompleteAsync permanently binds to one FHIR patient. Never validated as an origin (unlike callerId): it
        // is not a URL and never drives a redirect.
        var effectiveUserIdentity = !string.IsNullOrWhiteSpace(userIdentity) && userIdentity.Length <= 200 ? userIdentity : null;

        // See OAuthController's matching call: the encrypted launch context is the only channel that carries this
        // attempt's correlation id across the MyChart redirect into /oauth/callback.
        var correlationId = Request.Headers["X-Correlation-Id"].FirstOrDefault() is { Length: > 0 and <= 200 } supplied
            ? supplied
            : null;
        var context = _authorizationService.BuildWorkflowLaunchContextToken(
            workflowId, ehrEndpointId, callerId, effectiveSessionId, effectiveUserIdentity, correlationId);
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
