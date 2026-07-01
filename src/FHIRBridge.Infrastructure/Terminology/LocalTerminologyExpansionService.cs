using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Expands ValueSets known to the in-process <see cref="UsCoreValueSetCatalog"/>; empty for any others.</summary>
public sealed class LocalTerminologyExpansionService : ITerminologyExpansionService
{
    public Task<IReadOnlyList<TerminologyConcept>> ExpandAsync(string valueSetUrl, CancellationToken cancellationToken)
    {
        IReadOnlyList<TerminologyConcept> concepts = UsCoreValueSetCatalog.TryGet(valueSetUrl, out var definition)
            ? definition.ToConcepts()
            : [];

        return Task.FromResult(concepts);
    }
}
