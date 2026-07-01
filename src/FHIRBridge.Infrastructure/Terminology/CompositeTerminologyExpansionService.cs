using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Expands from the in-process catalog first, then the FHIR ValueSet/$expand server.</summary>
public sealed class CompositeTerminologyExpansionService : ITerminologyExpansionService
{
    private readonly LocalTerminologyExpansionService _localExpansionService;
    private readonly FhirTerminologyExpansionService _fhirExpansionService;

    public CompositeTerminologyExpansionService(
        LocalTerminologyExpansionService localExpansionService,
        FhirTerminologyExpansionService fhirExpansionService)
    {
        _localExpansionService = localExpansionService;
        _fhirExpansionService = fhirExpansionService;
    }

    public async Task<IReadOnlyList<TerminologyConcept>> ExpandAsync(string valueSetUrl, CancellationToken cancellationToken)
    {
        var local = await _localExpansionService.ExpandAsync(valueSetUrl, cancellationToken);
        return local.Count > 0
            ? local
            : await _fhirExpansionService.ExpandAsync(valueSetUrl, cancellationToken);
    }
}
