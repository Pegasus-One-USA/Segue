namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Encrypts and decrypts the identifiers carried in the interactive-OAuth URLs so raw route GUIDs are never
/// exposed in a launch URL registered with an EHR or in the OAuth <c>state</c> round-tripped through the EHR. The
/// server protects the values when building a URL and unprotects them (tamper-checked) when a request comes back.
/// </summary>
public interface ILaunchTokenProtector
{
    /// <summary>Encrypts the routeId (and, optionally, a hospital/organization EhrEndpoint id to launch against)
    /// into an opaque, URL-safe token for the registered launch URL.</summary>
    string ProtectContext(Guid routeId, Guid? ehrEndpointId = null);

    /// <summary>Encrypts a workflowId (and, optionally, a hospital/organization EhrEndpoint id to launch against)
    /// into an opaque launch token (launch runs the referenced workflow graph).</summary>
    string ProtectWorkflowContext(Guid workflowId, Guid? ehrEndpointId = null);

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
/// third-party app carries a user's hospital selection through the launch.</summary>
public sealed record LaunchContext(Guid? RouteId, Guid? WorkflowId = null, Guid? TargetNodeId = null, Guid? EhrEndpointId = null);
