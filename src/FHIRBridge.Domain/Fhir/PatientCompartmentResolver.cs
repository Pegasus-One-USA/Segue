namespace FHIRBridge.Domain.Fhir;

/// <summary>
/// Resolves the aggregation <c>include</c> query parameter into the deterministic set of patient-compartment
/// resource types to query. <c>Patient</c> is always excluded from the returned set — the Patient root is read
/// separately (<c>Patient?_id={id}</c>), not as a compartment query.
/// </summary>
public static class PatientCompartmentResolver
{
    private const string AllToken = "all";

    /// <summary>
    /// Resolves <paramref name="include"/> to the compartment types to query.
    /// <list type="bullet">
    /// <item><c>all</c> (case-insensitive), null, or whitespace → every compartment type (Patient excluded).</item>
    /// <item>CSV → each token trimmed, normalized, and deduped case-insensitively (Patient excluded).</item>
    /// </list>
    /// </summary>
    /// <exception cref="UnsupportedResourceTypeException">A token is not a supported resource type.</exception>
    public static IReadOnlyList<string> Resolve(string? include)
    {
        if (string.IsNullOrWhiteSpace(include) ||
            string.Equals(include.Trim(), AllToken, StringComparison.OrdinalIgnoreCase))
        {
            return AllCompartmentTypes();
        }

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawToken in include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!PatientCompartmentResourceTypes.IsSupported(rawToken))
            {
                throw new UnsupportedResourceTypeException(rawToken);
            }

            var normalized = PatientCompartmentResourceTypes.Normalize(rawToken);

            // Patient root is read separately; never include it in the compartment query set.
            if (string.Equals(normalized, "Patient", StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                resolved.Add(normalized);
            }
        }

        return resolved;
    }

    private static IReadOnlyList<string> AllCompartmentTypes()
    {
        return PatientCompartmentResourceTypes.All
            .Where(type => !string.Equals(type, "Patient", StringComparison.Ordinal))
            .ToArray();
    }
}
