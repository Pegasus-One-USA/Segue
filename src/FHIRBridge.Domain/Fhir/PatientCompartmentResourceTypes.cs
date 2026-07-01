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
    /// <c>Patient?_id={id}</c>); all other types are patient-compartment scoped.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        "Patient",
        "Encounter",
        "Observation",
        "Condition",
        "MedicationRequest",
        "AllergyIntolerance",
        "Immunization",
        "Procedure",
        "DiagnosticReport",
        "DocumentReference"
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
