using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Drives the interactive OAuth sign-in for a source connection: <see cref="StartAsync"/> prepares the authorization
/// redirect (discovering the source's SMART endpoints and stashing the PKCE/state), and <see cref="CompleteAsync"/>
/// completes it from the OAuth callback, exchanging the code for a token that later pipeline runs read back.
/// </summary>
public interface IInteractiveSourceAuthorizationService
{
    /// <summary>
    /// Prepares an interactive sign-in for a source and returns the authorization-endpoint URL the caller should
    /// redirect the browser to. <paramref name="redirectUri"/> must be the absolute callback URL registered with the
    /// EHR; it is retained so the token exchange presents the identical value.
    /// </summary>
    Task<Uri> StartAsync(Guid sourceConnectionId, string redirectUri, CancellationToken cancellationToken);

    /// <summary>
    /// Prepares an EHR launch: validates the incoming issuer against the source's trusted-issuer allow-list, then
    /// returns the authorization-endpoint URL (carrying the launch scope + token) to redirect the browser to.
    /// </summary>
    Task<Uri> StartEhrLaunchAsync(
        Guid sourceConnectionId,
        string issuer,
        string launch,
        string redirectUri,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares an EHR launch from an encrypted launch-context token (which resolves to the pipeline route).
    /// Resolves the source from the route, validates the issuer, and — on callback — the resolved route is run.
    /// </summary>
    Task<Uri> StartEhrLaunchFromContextAsync(
        string launchContext,
        string issuer,
        string launch,
        string redirectUri,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares a provider-standalone sign-in from an encrypted launch-context token (which resolves to the pipeline
    /// route). Unlike an EHR launch there is no issuer or launch token: the source's configured FHIR base URL is the
    /// authorization audience, and the EHR presents its own patient picker (driven by the patient-selection scope).
    /// On callback the resolved route is run for the selected patient.
    /// </summary>
    Task<Uri> StartStandaloneFromContextAsync(
        string launchContext,
        string redirectUri,
        CancellationToken cancellationToken);

    /// <summary>Completes a sign-in from the OAuth callback, exchanging the code (with the retained PKCE verifier) for a token, and triggering the launched pipeline route when the launch was route-scoped.</summary>
    Task<InteractiveAuthorizationResult> CompleteAsync(string state, string authorizationCode, CancellationToken cancellationToken);

    /// <summary>Builds the opaque, encrypted launch-context token to embed in the launch URL registered with the EHR
    /// for a given pipeline route. <paramref name="ehrEndpointId"/> optionally names a specific hospital/organization
    /// EhrEndpoint to launch against instead of the source connection's own configured base URL. <paramref name="callerId"/>
    /// optionally carries the caller-supplied return URL (e.g. the third-party app requesting this launch URL) to
    /// redirect to on completion instead of the source's static PostLaunchRedirectUri.</summary>
    string BuildLaunchContextToken(Guid routeId, Guid? ehrEndpointId = null, string? callerId = null);

    /// <summary>Builds the opaque, encrypted launch token for a workflow graph: launching it runs that workflow (its
    /// source node's connection drives the OAuth + trusted-issuer validation). <paramref name="ehrEndpointId"/>
    /// optionally names a specific hospital/organization EhrEndpoint to launch against. <paramref name="callerId"/>
    /// optionally carries the caller-supplied return URL (e.g. the third-party app requesting this launch URL) to
    /// redirect to on completion instead of the source's static PostLaunchRedirectUri.</summary>
    string BuildWorkflowLaunchContextToken(Guid workflowId, Guid? ehrEndpointId = null, string? callerId = null);

    /// <summary>
    /// Starts a standalone / patient interactive sign-in directly from an encrypted launch-context token — no EHR
    /// <c>iss</c>/<c>launch</c> required (the app is opened directly by a clinician or patient). Resolves the source
    /// from the workflow/route in the context; on callback the launched workflow/route is run. Throws if the resolved
    /// source is configured for EHR launch (which must go through the iss/launch entry point instead).
    /// </summary>
    Task<Uri> StartInteractiveFromContextAsync(string launchContext, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Resolves the SMART application type of the source behind a workflow (via its source node) — lets callers pick the right launch-URL shape.</summary>
    Task<ApplicationType?> GetWorkflowApplicationTypeAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Resolves the SMART application type of the source behind a pipeline route (via its mapping profile).</summary>
    Task<ApplicationType?> GetRouteApplicationTypeAsync(Guid routeId, CancellationToken cancellationToken);
}

/// <summary>
/// Which source connection was authorized once the callback completes. For a workflow-triggered launch,
/// <see cref="WorkflowRunId"/> carries the resulting run id and <see cref="PostLaunchRedirectUri"/> carries where the
/// caller should redirect the browser (the source's configured PostLaunchRedirectUri) instead of returning JSON.
/// <see cref="WorkflowRunFailed"/> is set when a workflow run was attempted but threw — the caller still redirects
/// (if a PostLaunchRedirectUri is configured), just with an error marker instead of a run id, so a third-party app
/// is never stranded on this endpoint's bare JSON response. <see cref="WorkflowRunSkipped"/> is set when a workflow
/// WAS bound but deliberately not attempted (a Standalone/patient-standalone sign-in with no upfront launch
/// context — see CompleteAsync's skipWorkflowTrigger) — the caller still redirects, but with a neutral "signed in"
/// marker rather than an error one, since nothing actually failed. All flags are false/null when no workflow run
/// was triggered at all (a plain sign-in) or no redirect URI is configured.
/// </summary>
public sealed record InteractiveAuthorizationResult(
    Guid SourceConnectionId,
    string SourceName,
    Guid? WorkflowRunId = null,
    string? PostLaunchRedirectUri = null,
    bool WorkflowRunFailed = false,
    bool WorkflowRunSkipped = false);
