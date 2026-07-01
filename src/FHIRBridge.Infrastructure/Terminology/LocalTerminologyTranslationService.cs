using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// In-process concept maps for the most common cross-system translations, so the pipeline can normalize codes
/// offline without a terminology server. Falls through (returns null) for anything not seeded; the composite
/// then tries the FHIR ConceptMap/$translate server.
/// </summary>
public sealed class LocalTerminologyTranslationService : ITerminologyTranslationService
{
    private const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string Snomed = "http://snomed.info/sct";
    private const string Loinc = "http://loinc.org";
    private const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";
    private const string Ndc = "http://hl7.org/fhir/sid/ndc";

    private static readonly IReadOnlyDictionary<string, (string TargetCode, string? Display)> Maps =
        new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase)
        {
            // --- Conditions: ICD-10-CM <-> SNOMED CT ---
            // Type 2 diabetes mellitus
            [Key(Icd10Cm, "E11.9", Snomed)] = ("44054006", "Type 2 diabetes mellitus"),
            [Key(Snomed, "44054006", Icd10Cm)] = ("E11.9", "Type 2 diabetes mellitus without complications"),
            // Essential hypertension
            [Key(Icd10Cm, "I10", Snomed)] = ("38341003", "Hypertensive disorder"),
            [Key(Snomed, "38341003", Icd10Cm)] = ("I10", "Essential (primary) hypertension"),

            // --- Lab observations: LOINC <-> SNOMED CT ---
            // Glucose
            [Key(Loinc, "2339-0", Snomed)] = ("33747003", "Glucose measurement"),
            [Key(Snomed, "33747003", Loinc)] = ("2339-0", "Glucose [Mass/volume] in Blood"),
            // Hemoglobin A1c
            [Key(Loinc, "4548-4", Snomed)] = ("43396009", "Hemoglobin A1c measurement"),
            [Key(Snomed, "43396009", Loinc)] = ("4548-4", "Hemoglobin A1c/Hemoglobin.total in Blood"),
            // Systolic blood pressure
            [Key(Loinc, "8480-6", Snomed)] = ("271649006", "Systolic blood pressure"),
            [Key(Snomed, "271649006", Loinc)] = ("8480-6", "Systolic blood pressure"),

            // --- Medications: NDC -> RxNorm and RxNorm <-> SNOMED ---
            // Lisinopril 10 MG Oral Tablet
            [Key(Ndc, "00071-0156", RxNorm)] = ("314076", "Lisinopril 10 MG Oral Tablet"),
            [Key(RxNorm, "314076", Snomed)] = ("318900008", "Lisinopril 10mg tablet"),
            // Metformin 500 MG Oral Tablet
            [Key(Ndc, "00093-7214", RxNorm)] = ("861007", "Metformin hydrochloride 500 MG Oral Tablet"),
            [Key(RxNorm, "861007", Snomed)] = ("325278007", "Metformin 500mg tablet"),
        };

    public Task<TerminologyTranslationResult?> TranslateAsync(
        string sourceSystem,
        string sourceCode,
        string targetSystem,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceSystem) || string.IsNullOrWhiteSpace(sourceCode) || string.IsNullOrWhiteSpace(targetSystem))
        {
            return Task.FromResult<TerminologyTranslationResult?>(null);
        }

        if (!Maps.TryGetValue(Key(sourceSystem, sourceCode, targetSystem), out var match))
        {
            return Task.FromResult<TerminologyTranslationResult?>(null);
        }

        return Task.FromResult<TerminologyTranslationResult?>(new TerminologyTranslationResult(
            sourceSystem.Trim(),
            sourceCode.Trim(),
            targetSystem.Trim(),
            match.TargetCode,
            match.Display,
            "Local"));
    }

    private static string Key(string sourceSystem, string sourceCode, string targetSystem)
        => $"{sourceSystem.Trim()}|{sourceCode.Trim()}|{targetSystem.Trim()}";
}
