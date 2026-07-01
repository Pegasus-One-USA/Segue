using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Terminology;

/// <summary>
/// Expands a ValueSet to its member concepts (FHIR <c>ValueSet/$expand</c>). Returns an empty list when the ValueSet
/// is unknown locally and no terminology server is configured.
/// </summary>
public interface ITerminologyExpansionService
{
    Task<IReadOnlyList<TerminologyConcept>> ExpandAsync(
        string valueSetUrl,
        CancellationToken cancellationToken);
}
