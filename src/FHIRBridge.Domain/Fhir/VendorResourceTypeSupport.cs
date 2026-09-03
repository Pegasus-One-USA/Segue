using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Domain.Fhir;

/// <summary>
/// FHIR resource types each source vendor's real API is confirmed to support, independent of what
/// <see cref="SupportedFhirResourceTypes"/> allows the platform to process. Used to narrow the portal's
/// resource-type picker so a user can't select a resource that vendor's live FHIR server is guaranteed
/// to reject. A vendor absent from <see cref="ByVendor"/> has no known restriction — callers should treat
/// that as "no filter, fall back to <see cref="SupportedFhirResourceTypes.All"/>" (e.g. Epic, whose App
/// Orchard catalog already covers everything this platform supports; GenericFhir, which varies by
/// deployment and can't be pinned to one list).
/// </summary>
public static class VendorResourceTypeSupport
{
    // Athenahealth: verified 2026-09 against the live athenaPractice CapabilityStatement
    // (docs.mydata.athenahealth.com/fhir-r4/CapabilityStatement-metadata-aPractice.html, v25.0.0).
    // Healow (eClinicalWorks): verified 2026-09 against eCW's public developer documentation
    // (fhir.eclinicalworks.com/ecwopendev/documentation) — a docs summary, not a raw CapabilityStatement
    // JSON; reconfirm against a live GET /fhir/r4/metadata before relying on this for a build decision.
    private static readonly IReadOnlyDictionary<SourceSystemType, IReadOnlyList<string>> ByVendor =
        new Dictionary<SourceSystemType, IReadOnlyList<string>>
        {
            [SourceSystemType.Athenahealth] =
            [
                "Account", "AllergyIntolerance", "Appointment", "Binary", "CarePlan", "CareTeam",
                "Condition", "Consent", "Coverage", "Device", "DiagnosticReport", "DocumentReference",
                "Encounter", "Endpoint", "FamilyMemberHistory", "Goal", "Group", "Immunization", "List",
                "Location", "Media", "Medication", "MedicationAdministration", "MedicationDispense",
                "MedicationRequest", "Observation", "Organization", "Patient", "Practitioner",
                "PractitionerRole", "Procedure", "Provenance", "QuestionnaireResponse", "RelatedPerson",
                "ServiceRequest", "Specimen",
            ],
            [SourceSystemType.Healow] =
            [
                "AllergyIntolerance", "Binary", "CarePlan", "CareTeam", "Communication", "Condition",
                "Coverage", "Device", "DiagnosticReport", "DocumentReference", "Encounter",
                "FamilyMemberHistory", "Goal", "Immunization", "Location", "Media", "Medication",
                "MedicationAdministration", "MedicationDispense", "MedicationRequest", "Observation",
                "Organization", "Patient", "Practitioner", "PractitionerRole", "Procedure", "Provenance",
                "Questionnaire", "QuestionnaireResponse", "RelatedPerson", "ServiceRequest", "Specimen",
                "Task",
            ],
        };

    /// <summary>Returns the vendor's supported resource types, or null when no restriction is known.</summary>
    public static IReadOnlyList<string>? For(SourceSystemType vendor) =>
        ByVendor.TryGetValue(vendor, out var list) ? list : null;

    /// <summary>String-keyed overload for callers holding an unparsed vendor name (e.g. an API query
    /// parameter) rather than the enum — mirrors the parsing MappingController already does for
    /// sourceVendor. Unknown/unparsable names are treated as "no restriction", same as an unlisted enum
    /// value, so a typo degrades to showing everything rather than an error.</summary>
    public static IReadOnlyList<string>? For(string? vendor) =>
        !string.IsNullOrWhiteSpace(vendor) && Enum.TryParse<SourceSystemType>(vendor, ignoreCase: true, out var parsed)
            ? For(parsed)
            : null;
}
