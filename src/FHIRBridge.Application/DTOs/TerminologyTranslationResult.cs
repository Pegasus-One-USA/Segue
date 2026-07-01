namespace FHIRBridge.Application.DTOs;

/// <summary>Result of translating a code from one code system to another (FHIR ConceptMap/$translate).</summary>
public sealed record TerminologyTranslationResult(
    string SourceSystem,
    string SourceCode,
    string TargetSystem,
    string TargetCode,
    string? TargetDisplay,
    string Source);
