namespace FHIRBridge.Domain.Fhir;

/// <summary>
/// The set of FHIR resource types the platform currently supports for configuration and pipeline processing.
/// Mirrors the Firely <c>ModelInfo.SupportedResources</c> / <c>IsKnownResource</c> convention.
/// </summary>
public static class SupportedFhirResourceTypes
{
    public static readonly IReadOnlyList<string> All =
    [
        "Patient",
        "Practitioner",
        "Observation",
        "Condition",
        "MedicationRequest",
        "MedicationAdministration",
        "AllergyIntolerance",
        "Encounter",
        "DiagnosticReport",
        "Procedure",
        "ServiceRequest",
        "Immunization",
        "Appointment",
        "CarePlan",
        "CareTeam",
        "Communication",
        "CommunicationRequest",
        "Device",
        "DocumentReference",
        "FamilyMemberHistory",
        "ImagingStudy",
        "Location",
        "Medication",
        "MedicationDispense",
        "MedicationStatement",
        "Organization",
        "PractitionerRole",
        "Provenance",
        "Questionnaire",
        "QuestionnaireResponse",
        "RelatedPerson",
        "Schedule",
        "Slot",
        "Specimen",
        "Task"
    ];

    private static readonly IReadOnlyDictionary<string, string> Normalized =
        All.ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string resourceType)
    {
        return Normalized.ContainsKey(resourceType);
    }

    public static string Normalize(string resourceType)
    {
        if (!Normalized.TryGetValue(resourceType, out var normalized))
        {
            throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, "FHIR resource type is not supported.");
        }

        return normalized;
    }
}
