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
        string? patientSearchCriteria = null);

    /// <summary>Discards any cached interactive token for this source connection (both the given patient's slot, if
    /// any, and the unscoped "default" slot) — a no-op for non-interactive (Backend Services) sources, since those
    /// mint tokens on demand rather than caching one. Returns without effect if the connection doesn't exist.</summary>
    Task DiscardTokenAsync(Guid sourceConnectionId, string? targetPatientId, CancellationToken cancellationToken);
}
