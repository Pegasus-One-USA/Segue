namespace FHIRBridge.Application.Services.Mapping.Internal;

/// <summary>
/// Immutable, FHIR-aware synonym table: groups of normalized terms (see <see cref="FieldNormalizer"/>) that
/// mean the same clinical/administrative concept even though their names differ (<c>dob</c> vs <c>BirthDate</c>,
/// <c>mrn</c> vs <c>Identifier</c>). Built once at process start; lookups are O(1) dictionary hits so this stays
/// cheap across thousands of comparisons per request.
/// </summary>
public static class SynonymDictionary
{
    private static readonly string[][] Groups =
    [
        ["dob", "date of birth", "birth date", "birthdate"],
        ["sex", "gender", "administrative gender"],
        ["mrn", "medical record number", "patient id", "patientid", "identifier"],
        ["phone", "mobile", "telecom", "phone number", "mobile number", "contact number"],
        ["firstname", "first name", "given name", "given"],
        ["lastname", "last name", "family name", "family", "surname"],
        ["diagnosis code", "condition code", "dx code"],
        ["visit date", "encounter period start", "encounter start", "encounter date"],
        ["provider id", "practitioner identifier", "npi", "national provider identifier"],
        ["email", "email address", "electronic mail"],
        ["address", "street address", "home address"],
        ["ssn", "social security number"],
        ["dod", "date of death", "death date"],
        ["marital status", "civil status"],
        ["race", "ethnicity"],
        ["lab result", "observation value", "test result"]
    ];

    private static readonly IReadOnlyDictionary<string, int> TermToGroup = BuildIndex();

    private static IReadOnlyDictionary<string, int> BuildIndex()
    {
        var index = new Dictionary<string, int>();
        for (var groupIndex = 0; groupIndex < Groups.Length; groupIndex++)
        {
            foreach (var term in Groups[groupIndex])
            {
                index[term] = groupIndex;
            }
        }

        return index;
    }

    /// <summary>1.0 if both normalized names belong to the same synonym group (or are identical), else 0.0.</summary>
    public static double Score(string normalizedSource, string normalizedDestination)
    {
        if (normalizedSource.Length == 0 || normalizedDestination.Length == 0)
        {
            return 0.0;
        }

        if (normalizedSource == normalizedDestination)
        {
            return 1.0;
        }

        if (!TermToGroup.TryGetValue(normalizedSource, out var sourceGroup))
        {
            return 0.0;
        }

        return TermToGroup.TryGetValue(normalizedDestination, out var destinationGroup) && sourceGroup == destinationGroup
            ? 1.0
            : 0.0;
    }
}
