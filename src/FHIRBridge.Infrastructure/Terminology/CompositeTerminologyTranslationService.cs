using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Tries the in-process concept maps first, then the FHIR ConceptMap/$translate server.</summary>
public sealed class CompositeTerminologyTranslationService : ITerminologyTranslationService
{
    private readonly LocalTerminologyTranslationService _localTranslationService;
    private readonly FhirTerminologyTranslationService _fhirTranslationService;

    public CompositeTerminologyTranslationService(
        LocalTerminologyTranslationService localTranslationService,
        FhirTerminologyTranslationService fhirTranslationService)
    {
        _localTranslationService = localTranslationService;
        _fhirTranslationService = fhirTranslationService;
    }

    public async Task<TerminologyTranslationResult?> TranslateAsync(
        string sourceSystem,
        string sourceCode,
        string targetSystem,
        CancellationToken cancellationToken)
    {
        return await _localTranslationService.TranslateAsync(sourceSystem, sourceCode, targetSystem, cancellationToken)
            ?? await _fhirTranslationService.TranslateAsync(sourceSystem, sourceCode, targetSystem, cancellationToken);
    }
}
