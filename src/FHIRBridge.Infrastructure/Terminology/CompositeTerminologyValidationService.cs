using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Validates against the in-process catalog first, then the FHIR ValueSet/$validate-code server.</summary>
public sealed class CompositeTerminologyValidationService : ITerminologyValidationService
{
    private readonly LocalTerminologyValidationService _localValidationService;
    private readonly FhirTerminologyValidationService _fhirValidationService;

    public CompositeTerminologyValidationService(
        LocalTerminologyValidationService localValidationService,
        FhirTerminologyValidationService fhirValidationService)
    {
        _localValidationService = localValidationService;
        _fhirValidationService = fhirValidationService;
    }

    public async Task<TerminologyValidationResult?> ValidateCodeAsync(
        string valueSetUrl,
        string? system,
        string code,
        CancellationToken cancellationToken)
    {
        return await _localValidationService.ValidateCodeAsync(valueSetUrl, system, code, cancellationToken)
            ?? await _fhirValidationService.ValidateCodeAsync(valueSetUrl, system, code, cancellationToken);
    }
}
