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
        CancellationToken cancellationToken);
}
