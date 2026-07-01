namespace FHIRBridge.Runtime.Domain.Fhir;

/// <summary>
/// The set of FHIR resource types the runtime pipeline currently supports.
/// Mirrors the Firely <c>ModelInfo.SupportedResources</c> / <c>IsKnownResource</c> convention.
/// </summary>
public static class SupportedFhirResourceTypes
{
    public static readonly IReadOnlyList<string> All =
    [
        "Patient",
        "Observation",
        "Condition",
        "MedicationRequest",
        "AllergyIntolerance",
        "Encounter",
        "DiagnosticReport",
        "Procedure",
        "Immunization"
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
