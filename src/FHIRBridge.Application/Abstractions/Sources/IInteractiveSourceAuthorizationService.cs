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
}

/// <summary>Which source connection was authorized once the callback completes.</summary>
public sealed record InteractiveAuthorizationResult(Guid SourceConnectionId, string SourceName);
