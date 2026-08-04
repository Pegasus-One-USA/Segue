using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

public sealed class CompositeTerminologyLookupService : ITerminologyLookupService
{
    private readonly LocalTerminologyLookupService _localLookupService;
    private readonly LoincTerminologyLookupService _loincLookupService;
    private readonly FhirTerminologyLookupService _fhirLookupService;

    public CompositeTerminologyLookupService(
        LocalTerminologyLookupService localLookupService,
        LoincTerminologyLookupService loincLookupService,
        FhirTerminologyLookupService fhirLookupService)
    {
        _localLookupService = localLookupService;
        _loincLookupService = loincLookupService;
        _fhirLookupService = fhirLookupService;
    }

    public async Task<TerminologyLookupResult?> LookupAsync(
        string system,
        string code,
        CancellationToken cancellationToken)
    {
        return await _localLookupService.LookupAsync(system, code, cancellationToken)
            ?? await _loincLookupService.LookupAsync(system, code, cancellationToken)
            ?? await _fhirLookupService.LookupAsync(system, code, cancellationToken);
    }
}
