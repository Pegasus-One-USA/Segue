namespace FHIRBridge.Application.Services.Mapping.Internal;

/// <summary>
/// Jaro-Winkler string similarity for normalized field names, plus a small table of known structural path
/// patterns (<c>name.given</c> → <c>GivenName</c>) for structural-similarity scoring.
/// </summary>
public static class SimilarityEngine
{
    // Structural path (normalized, see FieldNormalizer.NormalizeStructuralPath) -> the destination concept it
    // typically maps to. A hit means "this source shape strongly implies that destination field."
    private static readonly IReadOnlyDictionary<string, string> StructuralPatterns = new Dictionary<string, string>
    {
        ["name given"] = "given name",
        ["name family"] = "family name",
        ["address city"] = "address city",
        ["address line"] = "address line",
        ["address state"] = "address state",
        ["address postal code"] = "postal code",
        ["telecom value"] = "phone",
        ["identifier value"] = "identifier",
        ["code coding code"] = "code",
        ["code coding system"] = "code system",
        ["code coding display"] = "code display",
        ["subject reference"] = "patient reference",
        ["value quantity value"] = "value",
        ["value quantity unit"] = "unit"
    };

    public static double NameScore(string normalizedA, string normalizedB)
    {
        if (normalizedA.Length == 0 || normalizedB.Length == 0)
        {
            return 0.0;
        }

        if (normalizedA == normalizedB)
        {
            return 1.0;
        }

        return JaroWinkler(normalizedA, normalizedB);
    }

    public static double StructuralScore(string normalizedStructuralPath, string normalizedDestinationName)
    {
        if (normalizedStructuralPath.Length == 0 ||
            !StructuralPatterns.TryGetValue(normalizedStructuralPath, out var expectedConcept))
        {
            return 0.0;
        }

        return NameScore(expectedConcept, normalizedDestinationName);
    }

    /// <summary>Standard Jaro-Winkler distance, 0.0 (no similarity) to 1.0 (identical).</summary>
    public static double JaroWinkler(string a, string b)
    {
        var jaro = Jaro(a, b);
        if (jaro <= 0.0)
        {
            return 0.0;
        }

        var prefixLength = 0;
        var maxPrefix = Math.Min(4, Math.Min(a.Length, b.Length));
        while (prefixLength < maxPrefix && a[prefixLength] == b[prefixLength])
        {
            prefixLength++;
        }

        const double scalingFactor = 0.1;
        return jaro + prefixLength * scalingFactor * (1 - jaro);
    }

    private static double Jaro(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        var matchDistance = Math.Max(a.Length, b.Length) / 2 - 1;
        matchDistance = Math.Max(matchDistance, 0);

        var aMatches = new bool[a.Length];
        var bMatches = new bool[b.Length];
        var matches = 0;

        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, b.Length);
            for (var j = start; j < end; j++)
            {
                if (bMatches[j] || a[i] != b[j])
                {
                    continue;
                }

                aMatches[i] = true;
                bMatches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0.0;
        }

        double transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatches[i])
            {
                continue;
            }

            while (!bMatches[k])
            {
                k++;
            }

            if (a[i] != b[k])
            {
                transpositions++;
            }

            k++;
        }

        transpositions /= 2;

        return ((double)matches / a.Length
                + (double)matches / b.Length
                + (matches - transpositions) / matches) / 3.0;
    }
}
