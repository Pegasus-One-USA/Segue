namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Encrypts and decrypts the identifiers carried in the interactive-OAuth URLs so raw route GUIDs are never
/// exposed in a launch URL registered with an EHR or in the OAuth <c>state</c> round-tripped through the EHR. The
/// server protects the values when building a URL and unprotects them (tamper-checked) when a request comes back.
/// </summary>
public interface ILaunchTokenProtector
{
    /// <summary>Encrypts the routeId (and, optionally, a hospital/organization EhrEndpoint id to launch against, a
    /// caller-supplied callerId — the URL to redirect to on completion instead of the source's static
    /// PostLaunchRedirectUri — a caller-supplied sessionId, see <see cref="LaunchContext.SessionId"/>, and a
    /// caller-supplied userIdentity, see <see cref="LaunchContext.UserIdentity"/>) into an opaque, URL-safe token for
    /// the registered launch URL.</summary>
    string ProtectContext(Guid routeId, Guid? ehrEndpointId = null, string? callerId = null, string? sessionId = null, string? userIdentity = null, string? correlationId = null);

    /// <summary>Encrypts a workflowId (and, optionally, a hospital/organization EhrEndpoint id to launch against, a
    /// caller-supplied callerId — the URL to redirect to on completion instead of the source's static
    /// PostLaunchRedirectUri — a caller-supplied sessionId, see <see cref="LaunchContext.SessionId"/>, and a
    /// caller-supplied userIdentity, see <see cref="LaunchContext.UserIdentity"/>) into an opaque launch token
    /// (launch runs the referenced workflow graph).</summary>
    string ProtectWorkflowContext(Guid workflowId, Guid? ehrEndpointId = null, string? callerId = null, string? sessionId = null, string? userIdentity = null, string? correlationId = null);

    /// <summary>Encrypts a (workflowId, targetNodeId) pair into an opaque checkpoint-launch token — hitting it runs
    /// only that node's ancestor closure, not the full workflow graph.</summary>
    string ProtectWorkflowCheckpointContext(Guid workflowId, Guid targetNodeId);

    /// <summary>Decrypts a launch-context token; returns null if it is malformed or tampered.</summary>
    LaunchContext? UnprotectContext(string token);

    /// <summary>Encrypts the OAuth state (an opaque nonce) into a tamper-proof token sent to the EHR.</summary>
    string ProtectState(string nonce);

    /// <summary>Decrypts an OAuth state token back to its nonce; returns null if malformed or tampered.</summary>
    string? UnprotectState(string token);
}

/// <summary>What a launch URL resolves to: a pipeline route, a full workflow graph, or a single checkpoint node
/// within a workflow graph (when <see cref="TargetNodeId"/> is set, <see cref="WorkflowId"/> is also set).
/// <see cref="EhrEndpointId"/> optionally names a specific hospital/organization endpoint (from the <c>EhrEndpoints</c>
/// directory) to launch against instead of the bound source connection's own configured base URL — set when a
/// third-party app carries a user's hospital selection through the launch. <see cref="CallerId"/> optionally carries
/// the caller-supplied return URL (e.g. the third-party app that requested this launch URL) to redirect to on
/// completion instead of the source's static PostLaunchRedirectUri — a UI concern, distinct from
/// <see cref="SessionId"/>. <see cref="SessionId"/> optionally carries an opaque identifier minted by FHIRBridge
/// (echoed back by the caller on this and every later token-status/run/discard-token call for the same browser
/// session) that the Patient Standalone token cache keys on instead of SourceConnectionId — letting every pipeline
/// sharing one real patient's session reuse the one token their authorization already covers. Unlike CallerId, this
/// is never a URL and is never used for a redirect. <see cref="UserIdentity"/> optionally carries a stable identifier
/// for the end user driving this launch (e.g. a third-party app's own logged-in account email) — distinct from
/// SessionId (an opaque, per-browser token) and CallerId (a redirect URL): this is what a user-to-FHIR-context
/// binding is permanently keyed on, so it must identify the same real person across every launch, not just one
/// browser session. <see cref="CorrelationId"/> optionally carries the attempt-scoped correlation id minted by
/// <c>validate-run</c>, so the OAuth legs — which are browser redirects and can carry no header — are logged under
/// the same id as every other call in the same attempt.</summary>
public sealed record LaunchContext(Guid? RouteId, Guid? WorkflowId = null, Guid? TargetNodeId = null, Guid? EhrEndpointId = null, string? CallerId = null, string? SessionId = null, string? UserIdentity = null, string? CorrelationId = null);
