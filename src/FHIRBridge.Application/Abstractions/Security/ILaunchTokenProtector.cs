namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Encrypts and decrypts the identifiers carried in the interactive-OAuth URLs so raw route GUIDs are never
/// exposed in a launch URL registered with an EHR or in the OAuth <c>state</c> round-tripped through the EHR. The
/// server protects the values when building a URL and unprotects them (tamper-checked) when a request comes back.
/// </summary>
public interface ILaunchTokenProtector
{
    /// <summary>Encrypts the routeId into an opaque, URL-safe token for the registered launch URL.</summary>
    string ProtectContext(Guid routeId);

    /// <summary>Encrypts a workflowId into an opaque launch token (launch runs the referenced workflow graph).</summary>
    string ProtectWorkflowContext(Guid workflowId);

    /// <summary>Decrypts a launch-context token; returns null if it is malformed or tampered.</summary>
    LaunchContext? UnprotectContext(string token);

    /// <summary>Encrypts the OAuth state (an opaque nonce) into a tamper-proof token sent to the EHR.</summary>
    string ProtectState(string nonce);

    /// <summary>Decrypts an OAuth state token back to its nonce; returns null if malformed or tampered.</summary>
    string? UnprotectState(string token);
}

/// <summary>What a launch URL resolves to: either a pipeline route or a workflow graph (exactly one is set).</summary>
public sealed record LaunchContext(Guid? RouteId, Guid? WorkflowId = null);
