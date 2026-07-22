using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Sources;

/// <summary>
/// Resolves a persisted control-plane SourceConnection (by id) into the runtime <see cref="FhirSourceConfiguration"/>
/// a source node executor needs. This is how a builder-authored graph references a real connection by id and has the
/// live base URL / auth / token resolved at run time (Option A — reference, don't copy).
/// </summary>
public interface ISourceConnectionRuntimeResolver
{
    Task<FhirSourceConfiguration?> ResolveAsync(
        Guid sourceConnectionId,
        string? searchParameters,
        string? targetPatientId,
        CancellationToken cancellationToken,
        string? patientSearchCriteria = null,
        string? callerId = null);

    /// <summary>Discards any cached interactive token for this source connection (both the given patient's slot, if
    /// any, and the unscoped "default" slot) — a no-op for non-interactive (Backend Services) sources, since those
    /// mint tokens on demand rather than caching one. Returns without effect if the connection doesn't exist.</summary>
    Task DiscardTokenAsync(Guid sourceConnectionId, string? targetPatientId, CancellationToken cancellationToken, string? callerId = null);

    /// <summary>
    /// Cheaply checks whether a real fetch against this source connection would currently succeed authentication-wise
    /// — without running any pipeline. Reuses the exact same resolution and token-provider dispatch a real run would
    /// use (so this never disagrees with what /run would decide), but stops at "do we have/can we silently refresh a
    /// usable token" rather than actually searching for resources. For an interactive source this is a plain cache
    /// lookup (plus a real refresh-token call only if the cached access token has actually expired); for a
    /// Backend Services source a token can always be minted on demand, so this is effectively always true. Returns
    /// false if the connection doesn't exist or no token provider is configured.
    /// </summary>
    Task<bool> HasValidTokenAsync(Guid sourceConnectionId, string? targetPatientId, CancellationToken cancellationToken, string? callerId = null);
}
