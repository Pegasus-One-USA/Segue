namespace FHIRBridge.Domain.Fhir;

/// <summary>
/// The set of FHIR resource types the platform aggregates for a synchronous, patient-scoped read
/// (<c>GET .../fhirbridge/Patient/{id}?include=...</c>).
/// <para>
/// Deliberately kept SEPARATE from the write-pipeline <see cref="SupportedFhirResourceTypes"/>: read-aggregation
/// coverage evolves independently of what the pipeline can map/extract. Every type here is queryable via the
/// standard <c>{Type}?patient={id}</c> compartment search.
/// </para>
/// </summary>
public static class PatientCompartmentResourceTypes
{
    /// <summary>
    /// The aggregation allow-list. <c>Patient</c> is included for completeness (the root is read via
    /// <c>Patient?_id={id}</c>); all other types are patient-compartment scoped. Membership matches the HL7 FHIR
    /// R4 <c>CompartmentDefinition-patient</c> (http://hl7.org/fhir/R4/compartmentdefinition-patient.html) —
    /// every resource type here is queryable via <c>{Type}?patient={id}</c>. See
    /// <see cref="Fhir.SupportedFhirResourceTypes"/> for the platform's full 35-type set: the 10 types NOT
    /// searchable this way — 9 never in the Patient compartment at all per that spec (Practitioner,
    /// PractitionerRole, Organization, Location, Device, Medication, Questionnaire, Schedule, Slot) plus
    /// <c>Specimen</c> (genuinely a compartment member per spec, but Epic's own implementation rejects a
    /// <c>patient=</c> search for it — confirmed live: "Unknown parameter: PATIENT... Only an _ID search is
    /// allowed"). When one of these 10 is selected, <c>SourceNodeExecutors</c> fetches it via a single clean,
    /// unscoped request instead of patient-scoping it.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        "Patient",
        "Encounter",
        "Observation",
        "Condition",
        "MedicationRequest",
        "MedicationAdministration",
        "AllergyIntolerance",
        "Immunization",
        "Procedure",
        "ServiceRequest",
        "DiagnosticReport",
        "DocumentReference",
        "Appointment",
        "CarePlan",
        "CareTeam",
        "Communication",
        "CommunicationRequest",
        "FamilyMemberHistory",
        "ImagingStudy",
        "MedicationDispense",
        "MedicationStatement",
        "Provenance",
        "QuestionnaireResponse",
        "RelatedPerson",
        "Task"
    ];

    private static readonly IReadOnlyDictionary<string, string> Normalized =
        All.ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string resourceType)
    {
        return resourceType is not null && Normalized.ContainsKey(resourceType);
    }

    public static string Normalize(string resourceType)
    {
        if (resourceType is null || !Normalized.TryGetValue(resourceType, out var normalized))
        {
            throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, "FHIR resource type is not supported for patient aggregation.");
        }

        return normalized;
    }
}
