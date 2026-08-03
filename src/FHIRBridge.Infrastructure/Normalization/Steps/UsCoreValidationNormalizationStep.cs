using System.Text.Json;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Infrastructure.Terminology;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Normalization.Steps;

/// <summary>
/// Phase B — US Core validation. Tags the resource with its US Core profile, warns on missing required / must-support
/// elements (structural), and validates required code bindings against their ValueSet using
/// <see cref="ITerminologyValidationService"/> (FHIR <c>$validate-code</c> with an offline catalog fallback). When no
/// validation service is supplied, only the structural checks run.
/// </summary>
public sealed class UsCoreValidationNormalizationStep : IResourceNormalizationStep
{
    public int Order => 20;

    private readonly ILogger<UsCoreValidationNormalizationStep> _logger;
    private readonly ITerminologyValidationService? _validationService;

    public UsCoreValidationNormalizationStep(
        ILogger<UsCoreValidationNormalizationStep> logger,
        ITerminologyValidationService? validationService = null)
    {
        _logger = logger;
        _validationService = validationService;
    }

    // Profile + the element paths US Core marks as required / must-support for the common resources.
    private static readonly IReadOnlyDictionary<string, (string Profile, string[] RequiredPaths)> Profiles =
        new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["Patient"] = ("USCorePatientProfile", ["identifier", "name", "gender"]),
            ["Observation"] = ("USCoreObservationProfile", ["status", "code", "subject"]),
            ["Condition"] = ("USCoreConditionProfile", ["category", "code", "subject"]),
            ["MedicationRequest"] = ("USCoreMedicationRequestProfile", ["status", "intent", "subject"]),
            ["AllergyIntolerance"] = ("USCoreAllergyIntoleranceProfile", ["clinicalStatus", "code", "patient"]),
            ["Encounter"] = ("USCoreEncounterProfile", ["status", "class", "subject"]),
            ["DiagnosticReport"] = ("USCoreDiagnosticReportProfile", ["status", "code", "subject"]),
            ["Procedure"] = ("USCoreProcedureProfile", ["status", "code", "subject"]),
            ["Immunization"] = ("USCoreImmunizationProfile", ["status", "vaccineCode", "patient"]),
            // Tier-2 resources.
            ["Coverage"] = ("USCoreCoverageProfile", ["status", "beneficiary", "payor"]),
            ["Claim"] = ("ClaimProfile", ["status", "patient", "insurer", "provider"]),
            ["ExplanationOfBenefit"] = ("USCoreExplanationOfBenefitProfile", ["status", "patient", "insurer"]),
            ["Organization"] = ("USCoreOrganizationProfile", ["identifier", "name"]),
            ["Practitioner"] = ("USCorePractitionerProfile", ["identifier", "name"]),
            ["Location"] = ("USCoreLocationProfile", ["name"]),
            ["ServiceRequest"] = ("USCoreServiceRequestProfile", ["status", "intent", "code", "subject"]),
            ["MedicationAdministration"] = ("USCoreMedicationAdministrationProfile", ["status", "subject"]),
            // Tier-3 resources.
            ["CarePlan"] = ("USCoreCarePlanProfile", ["status", "intent", "subject"]),
            ["Goal"] = ("USCoreGoalProfile", ["lifecycleStatus", "description", "subject"]),
            ["DocumentReference"] = ("USCoreDocumentReferenceProfile", ["status", "type", "subject", "content"]),
            ["Subscription"] = ("SubscriptionProfile", ["status", "criteria", "channel"]),
            ["Group"] = ("GroupProfile", ["type", "actual"]),
            ["MeasureReport"] = ("MeasureReportProfile", ["status", "type", "measure"]),
        };

    // US Core required code bindings: top-level code-typed element -> the ValueSet it must be a member of.
    private static readonly IReadOnlyDictionary<string, (string Field, string ValueSetUrl)[]> CodeBindings =
        new Dictionary<string, (string, string)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Patient"] = [("gender", UsCoreValueSetCatalog.AdministrativeGender)],
            ["Observation"] = [("status", UsCoreValueSetCatalog.ObservationStatus)],
            ["Encounter"] = [("status", UsCoreValueSetCatalog.EncounterStatus)],
            ["MedicationRequest"] =
            [
                ("status", UsCoreValueSetCatalog.MedicationRequestStatus),
                ("intent", UsCoreValueSetCatalog.MedicationRequestIntent)
            ],
            ["Immunization"] = [("status", UsCoreValueSetCatalog.ImmunizationStatus)],
            ["Procedure"] = [("status", UsCoreValueSetCatalog.EventStatus)],
            ["DiagnosticReport"] = [("status", UsCoreValueSetCatalog.DiagnosticReportStatus)],
            // AllergyIntolerance.clinicalStatus is a CodeableConcept (not a plain string like the fields above), so
            // it isn't code-bound here — ReadString only extracts flat string-typed elements; a nested-code
            // extension to this validator would be needed to check it correctly.
            ["ServiceRequest"] = [("status", UsCoreValueSetCatalog.RequestStatus)],
            ["MedicationAdministration"] = [("status", UsCoreValueSetCatalog.MedicationAdministrationStatus)],
            // Tier-2 / Tier-3 status bindings (closed ValueSets validated offline).
            ["Coverage"] = [("status", UsCoreValueSetCatalog.FinancialResourceStatus)],
            ["Claim"] = [("status", UsCoreValueSetCatalog.FinancialResourceStatus)],
            ["ExplanationOfBenefit"] = [("status", UsCoreValueSetCatalog.ExplanationOfBenefitStatus)],
            ["Location"] = [("status", UsCoreValueSetCatalog.LocationStatus)],
            ["CarePlan"] = [("status", UsCoreValueSetCatalog.RequestStatus)],
            ["Goal"] = [("lifecycleStatus", UsCoreValueSetCatalog.GoalLifecycleStatus)],
            ["DocumentReference"] = [("status", UsCoreValueSetCatalog.DocumentReferenceStatus)],
            ["Subscription"] = [("status", UsCoreValueSetCatalog.SubscriptionStatus)],
            ["Group"] = [("type", UsCoreValueSetCatalog.GroupType)],
            ["MeasureReport"] = [("status", UsCoreValueSetCatalog.MeasureReportStatus)],
        };

    public async Task<ResourceNormalizationResult> ApplyAsync(
        ResourceNormalizationRequest request,
        ResourceNormalizationResult current,
        CancellationToken cancellationToken)
    {
        if (!Profiles.TryGetValue(request.ResourceType, out var profile))
        {
            return current;
        }

        var appliedProfiles = new List<string>(current.AppliedProfiles) { profile.Profile };
        var warnings = new List<string>(current.Warnings);

        try
        {
            using var document = JsonDocument.Parse(current.NormalizedJson);
            var root = document.RootElement;

            foreach (var path in profile.RequiredPaths)
            {
                if (!HasNonEmpty(root, path))
                {
                    warnings.Add($"US Core {request.ResourceType}: required element '{path}' is missing or empty.");
                }
            }

            // Required code-binding validation via $validate-code (when a validation service is available).
            if (_validationService is not null && CodeBindings.TryGetValue(request.ResourceType, out var bindings))
            {
                foreach (var (field, valueSetUrl) in bindings)
                {
                    var code = ReadString(root, field);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue; // missing-element case already covered by the structural check
                    }

                    var result = await _validationService.ValidateCodeAsync(valueSetUrl, system: null, code, cancellationToken);
                    if (result is { IsValid: false })
                    {
                        warnings.Add($"US Core {request.ResourceType}: code '{code}' in '{field}' is not a valid member of {valueSetUrl}.");
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "US Core validation skipped: resource {ResourceType} is not valid JSON.", request.ResourceType);
        }

        return current with
        {
            AppliedProfiles = appliedProfiles,
            Warnings = warnings
        };
    }

    private static string? ReadString(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasNonEmpty(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            JsonValueKind.Undefined => false,
            _ => true
        };
    }
}
