using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <inheritdoc cref="IParentReferenceResolver"/>
public sealed class ParentReferenceResolver : IParentReferenceResolver
{
    private readonly IFhirElementCatalog _catalog;

    public ParentReferenceResolver(IFhirElementCatalog catalog)
    {
        _catalog = catalog;
    }

    public FhirElementDto? Resolve(string childResourceType, string parentResourceType, string? referenceFieldOverride = null)
    {
        var fields = _catalog.Fields(childResourceType);

        if (!string.IsNullOrWhiteSpace(referenceFieldOverride))
        {
            return fields.FirstOrDefault(f => string.Equals(f.FhirPath, referenceFieldOverride, StringComparison.Ordinal));
        }

        var candidates = fields
            .Where(f => f.FhirPath.EndsWith(".reference", StringComparison.Ordinal))
            .Where(f => f.ReferenceTargetTypes.Contains(parentResourceType, StringComparer.Ordinal))
            .ToList();

        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }

        // Multiple fields could satisfy the same parent (e.g. Observation.subject and
        // Observation.performer can both target Patient) — break the tie structurally, without
        // hardcoding any resource or field name: prefer the most specific field (fewest allowed target
        // types), then a singular reference over a repeating one, then alphabetical as a final,
        // deterministic tiebreak.
        return candidates
            .OrderBy(f => f.ReferenceTargetTypes.Count)
            .ThenBy(f => f.Cardinality == "0..1" ? 0 : 1)
            .ThenBy(f => f.FhirPath, StringComparer.Ordinal)
            .First();
    }
}
