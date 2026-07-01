using FHIRBridge.Application.Abstractions.Normalization;

namespace FHIRBridge.Application.Services;

public sealed class PassThroughResourceNormalizationService : IResourceNormalizationService
{
    public Task<ResourceNormalizationResult> NormalizeAsync(
        ResourceNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> profiles = request.ResourceType switch
        {
            "Patient" => ["USCorePatientProfile"],
            "Observation" => ["USCoreObservationProfile"],
            "Condition" => ["USCoreConditionProfile"],
            "MedicationRequest" => ["USCoreMedicationRequestProfile"],
            "AllergyIntolerance" => ["USCoreAllergyIntoleranceProfile"],
            "Encounter" => ["USCoreEncounterProfile"],
            "DiagnosticReport" => ["USCoreDiagnosticReportProfile"],
            "Procedure" => ["USCoreProcedureProfile"],
            "Immunization" => ["USCoreImmunizationProfile"],
            _ => []
        };

        return Task.FromResult(new ResourceNormalizationResult(
            request.RawJson,
            profiles,
            []));
    }
}
