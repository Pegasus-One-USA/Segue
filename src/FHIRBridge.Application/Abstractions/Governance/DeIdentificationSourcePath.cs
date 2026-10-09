namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// The one definition of how a de-identification rule's SourceField addresses a resource element. Rules are
/// authored in three conventions — "$.name.given", bare "name.given" and resource-qualified "Patient.name.given" —
/// and with or without array indexers ("name[0].given", "name[*].given[*]"); all of them reduce to the same
/// property segments. Shared by the redaction itself (SafeHarborDeIdentificationService) and by the Mapping node's
/// check for which mapped column a redaction covers, so the two can never disagree about what a rule addresses.
/// </summary>
public static class DeIdentificationSourcePath
{
    /// <summary>The property segments <paramref name="sourceField"/> addresses within a
    /// <paramref name="resourceType"/> resource: the leading "$"/"$." and "{ResourceType}." are dropped, and each
    /// segment's indexer ("[0]", "[*]") is reduced to the bare property name.</summary>
    public static string[] Segments(string sourceField, string resourceType)
    {
        var path = sourceField.Trim();

        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        else if (path.StartsWith("$", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        var resourcePrefix = resourceType + ".";
        if (path.StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[resourcePrefix.Length..];
        }

        return path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripIndexer)
            .Where(segment => segment.Length > 0)
            .ToArray();
    }

    /// <summary>Whether two paths address the same element of a <paramref name="resourceType"/> resource.</summary>
    public static bool SameElement(string firstPath, string secondPath, string resourceType) =>
        Segments(firstPath, resourceType).SequenceEqual(Segments(secondPath, resourceType), StringComparer.Ordinal);

    /// <summary>Reduces "address[0]" / "address[*]" to "address".</summary>
    private static string StripIndexer(string segment)
    {
        var bracket = segment.IndexOf('[', StringComparison.Ordinal);
        return bracket < 0 ? segment : segment[..bracket].TrimEnd();
    }
}
