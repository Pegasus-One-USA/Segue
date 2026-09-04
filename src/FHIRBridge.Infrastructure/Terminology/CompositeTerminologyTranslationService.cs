using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Tries the in-process concept maps — that's the only tier. The old fallback to a remote FHIR
/// ConceptMap/$translate server was removed deliberately (not just disabled behind a setting): unlike
/// $lookup, this had no network-fallback kill switch at all, so a mapping field configured for a pair
/// outside the local map would hang for the same ~120s timeout that motivated the local terminology
/// cache in the first place. A miss now returns null immediately — no translation, not a slow one.</summary>
public sealed class CompositeTerminologyTranslationService : ITerminologyTranslationService
{
    private readonly LocalTerminologyTranslationService _localTranslationService;

    public CompositeTerminologyTranslationService(LocalTerminologyTranslationService localTranslationService)
    {
        _localTranslationService = localTranslationService;
    }

    public Task<TerminologyTranslationResult?> TranslateAsync(
        string sourceSystem,
        string sourceCode,
        string targetSystem,
        CancellationToken cancellationToken) =>
        _localTranslationService.TranslateAsync(sourceSystem, sourceCode, targetSystem, cancellationToken);
}
