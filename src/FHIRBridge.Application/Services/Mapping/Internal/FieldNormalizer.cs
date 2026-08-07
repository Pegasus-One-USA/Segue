using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace FHIRBridge.Application.Services.Mapping.Internal;

/// <summary>
/// Turns a raw field/column name (camelCase, snake_case, PascalCase, or a known clinical abbreviation) into a
/// lowercase, space-separated comparable form — e.g. <c>firstName</c>/<c>first_name</c>/<c>FirstName</c> all
/// normalize to <c>"first name"</c>, and <c>dob</c> expands to <c>"date of birth"</c>. Results are memoized:
/// the same handful of field names recur across thousands of pairwise comparisons in one request.
/// </summary>
public static class FieldNormalizer
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    private static readonly Regex ArrayIndexPattern = new(@"\[\d*\]", RegexOptions.Compiled);
    private static readonly Regex CamelBoundaryPattern = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumericPattern = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    // Common clinical/administrative abbreviations expanded to their full form before scoring. Deliberately a
    // plain lookup table (data), not branching code, so new abbreviations are additive.
    private static readonly IReadOnlyDictionary<string, string> Abbreviations = new Dictionary<string, string>
    {
        ["dob"] = "date of birth",
        ["mrn"] = "medical record number",
        ["ssn"] = "social security number",
        ["npi"] = "national provider identifier",
        ["dx"] = "diagnosis",
        ["rx"] = "prescription",
        ["dob date"] = "date of birth",
        ["tel"] = "telephone",
        ["fname"] = "first name",
        ["lname"] = "last name",
        ["mname"] = "middle name",
        ["dod"] = "date of death",
        ["ptid"] = "patient id",
        ["docid"] = "document id",
        ["hx"] = "history"
    };

    public static string Normalize(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        return Cache.GetOrAdd(rawName, static key =>
        {
            var withoutIndices = ArrayIndexPattern.Replace(key, string.Empty);
            var spaced = CamelBoundaryPattern.Replace(withoutIndices, " ").ToLowerInvariant();
            var lowered = NonAlphaNumericPattern.Replace(spaced, " ").Trim();
            var collapsed = string.Join(' ', lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            return Abbreviations.TryGetValue(collapsed, out var expanded) ? expanded : ExpandTokens(collapsed);
        });
    }

    private static string ExpandTokens(string collapsed)
    {
        var tokens = collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 1)
        {
            return collapsed;
        }

        return Abbreviations.TryGetValue(tokens[0], out var expanded) ? expanded : collapsed;
    }

    /// <summary>Normalizes a dotted structural path (e.g. <c>name.given</c>) into the same space-separated
    /// form used for field names (e.g. <c>"name given"</c>), for structural-pattern lookups.</summary>
    public static string NormalizeStructuralPath(string structuralPath)
    {
        if (string.IsNullOrWhiteSpace(structuralPath))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var segment in structuralPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Normalize(segment));
        }

        return builder.ToString();
    }
}
