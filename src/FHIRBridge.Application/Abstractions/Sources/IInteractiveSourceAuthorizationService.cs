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

    /// <summary>Completes a sign-in from the OAuth callback, exchanging the code (with the retained PKCE verifier) for a token, and triggering the launched pipeline route when the launch was route-scoped.</summary>
    Task<InteractiveAuthorizationResult> CompleteAsync(string state, string authorizationCode, CancellationToken cancellationToken);

    /// <summary>Builds the opaque, encrypted launch-context token to embed in the launch URL registered with the EHR for a given pipeline route.</summary>
    string BuildLaunchContextToken(Guid routeId);

    /// <summary>Builds the opaque, encrypted launch token for a workflow graph: launching it runs that workflow (its source node's connection drives the OAuth + trusted-issuer validation).</summary>
    string BuildWorkflowLaunchContextToken(Guid workflowId);

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

/// <summary>Which source connection was authorized once the callback completes.</summary>
public sealed record InteractiveAuthorizationResult(Guid SourceConnectionId, string SourceName);
