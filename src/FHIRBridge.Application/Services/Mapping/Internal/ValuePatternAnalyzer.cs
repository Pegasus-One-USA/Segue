using System.Text.RegularExpressions;

namespace FHIRBridge.Application.Services.Mapping.Internal;

/// <summary>
/// Detects the shape of a sample value (a date, a gender code, a phone number, a recognizable code-system
/// value) and reports which destination concepts that shape is consistent with. Backed entirely by compiled
/// static regexes — no allocation-heavy parsing on the hot path.
/// </summary>
public static class ValuePatternAnalyzer
{
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2})?", RegexOptions.Compiled);
    private static readonly Regex GenderCode = new(@"^[MFUOmfuo]$", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"^\+?[\d][\d\-\.\(\)\s]{6,}\d$", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex Icd10 = new(@"^[A-Za-z]\d{2}(\.\d{1,4})?$", RegexOptions.Compiled);
    private static readonly Regex Loinc = new(@"^\d{1,5}-\d$", RegexOptions.Compiled);
    private static readonly Regex Snomed = new(@"^\d{6,18}$", RegexOptions.Compiled);
    private static readonly Regex RxNorm = new(@"^\d{1,8}$", RegexOptions.Compiled);
    private static readonly Regex Npi = new(@"^\d{10}$", RegexOptions.Compiled);
    private static readonly Regex Mrn = new(@"^[A-Za-z]?\d{5,10}$", RegexOptions.Compiled);

    /// <summary>Destination concept keywords (already normalized, see FieldNormalizer) each detected pattern
    /// is consistent with — used to score a value-pattern hit against the actual destination field name/type.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> PatternDestinationHints = new Dictionary<string, string[]>
    {
        ["date"] = ["date", "birth", "death", "effective", "period", "visit", "recorded", "onset"],
        ["gender"] = ["gender", "sex"],
        ["phone"] = ["phone", "telecom", "mobile", "contact"],
        ["email"] = ["email", "telecom"],
        ["icd10"] = ["code", "condition", "diagnosis"],
        ["loinc"] = ["code", "observation"],
        ["snomed"] = ["code", "condition"],
        ["rxnorm"] = ["code", "medication"],
        ["npi"] = ["identifier", "provider", "practitioner"],
        ["mrn"] = ["identifier", "mrn"]
    };

    public static double Score(string? sampleValue, string normalizedDestinationName)
    {
        if (string.IsNullOrWhiteSpace(sampleValue))
        {
            return 0.0;
        }

        var pattern = DetectPattern(sampleValue);
        if (pattern is null || !PatternDestinationHints.TryGetValue(pattern, out var hints))
        {
            return 0.0;
        }

        return hints.Any(hint => normalizedDestinationName.Contains(hint, StringComparison.OrdinalIgnoreCase))
            ? 1.0
            : 0.3; // recognizable pattern, but destination name gives no corroborating hint
    }

    public static string? DetectPattern(string value)
    {
        var trimmed = value.Trim();
        if (IsoDate.IsMatch(trimmed))
        {
            return "date";
        }

        if (GenderCode.IsMatch(trimmed))
        {
            return "gender";
        }

        if (Email.IsMatch(trimmed))
        {
            return "email";
        }

        if (Icd10.IsMatch(trimmed))
        {
            return "icd10";
        }

        if (Loinc.IsMatch(trimmed))
        {
            return "loinc";
        }

        if (Npi.IsMatch(trimmed))
        {
            return "npi";
        }

        if (Phone.IsMatch(trimmed))
        {
            return "phone";
        }

        if (Snomed.IsMatch(trimmed))
        {
            return "snomed";
        }

        if (Mrn.IsMatch(trimmed) && trimmed.Any(char.IsLetter))
        {
            return "mrn";
        }

        return RxNorm.IsMatch(trimmed) ? "rxnorm" : null;
    }
}
