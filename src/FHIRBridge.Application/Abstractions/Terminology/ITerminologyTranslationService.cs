using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Terminology;

/// <summary>
/// Translates a code from one code system to an equivalent in another (e.g. ICD-10 ↔ SNOMED, NDC → RxNorm),
/// backed by FHIR ConceptMap/$translate and/or local concept maps.
/// </summary>
public interface ITerminologyTranslationService
{
    Task<TerminologyTranslationResult?> TranslateAsync(
        string sourceSystem,
        string sourceCode,
        string targetSystem,
        CancellationToken cancellationToken);
}
