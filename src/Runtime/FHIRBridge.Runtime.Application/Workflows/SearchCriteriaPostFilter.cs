using System.Text.Json;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// Re-applies the simple code-valued filters of a search's criteria to the records the source returned, dropping
/// those that don't match. A FHIR server ignores a search parameter it doesn't support and still answers 200
/// (lenient handling, the spec default) — athenahealth's Observation search, for one, has no <c>status</c>
/// parameter, so <c>status=final</c> came back with every status. Enforcing the filter here makes the criteria mean
/// the same thing on every source. Against a server that did apply it, this removes nothing. A bulk export
/// (<c>$export</c>) takes no search criteria at all, so for it this is the only way criteria take effect — see
/// <see cref="BulkExportCriteria"/>.
/// <para>
/// Deliberately narrow: only parameters named in <see cref="EnforceableParameters"/>, each a token search on the
/// top-level <c>code</c> element of the same name (Observation.status, MedicationRequest.intent, Patient.gender,
/// ...), where matching a code is unambiguous. Anything else in the criteria — references, dates with prefixes,
/// CodeableConcepts, chained or modified parameters other than <c>:not</c> — is left entirely to the server, since
/// evaluating it locally could drop records the server would have returned.
/// </para>
/// </summary>
public static class SearchCriteriaPostFilter
{
    /// <summary>Search parameter names that, wherever a resource type has them, search a top-level <c>code</c>
    /// element of the same name.</summary>
    public static readonly IReadOnlySet<string> EnforceableParameters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "status", "intent", "priority", "gender" };

    /// <summary>
    /// Returns <paramref name="records"/> without those failing an enforceable parameter in
    /// <paramref name="searchParameters"/>, plus the parameter names that were actually enforced. A parameter is
    /// only enforced when at least one record carries the element as a plain string: if none do, the element
    /// doesn't exist on this resource type (or isn't a <c>code</c>), and filtering on it would wrongly empty the
    /// result — it is left to the server instead.
    /// </summary>
    public static (IReadOnlyList<ResourceEnvelope> Records, IReadOnlyList<string> EnforcedParameters) Apply(
        IReadOnlyList<ResourceEnvelope> records,
        string? searchParameters)
    {
        var filters = ParseFilters(searchParameters);
        if (filters.Count == 0 || records.Count == 0)
        {
            return (records, []);
        }

        var parsed = records.Select(record => (Record: record, Values: ReadTopLevelStrings(record.RawJson, filters))).ToList();

        var enforced = filters
            .Where(filter => parsed.Any(item => item.Values.ContainsKey(filter.Element)))
            .ToList();
        if (enforced.Count == 0)
        {
            return (records, []);
        }

        var kept = parsed
            .Where(item => enforced.All(filter => filter.Matches(item.Values.GetValueOrDefault(filter.Element))))
            .Select(item => item.Record)
            .ToList();

        return (kept, enforced.Select(filter => filter.Parameter).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// The criteria a bulk-exported resource type's records are filtered by. A <c>$export</c> request carries no
    /// search criteria, so they can only take effect on what comes back: the type's own Criteria row
    /// (<paramref name="resourceCriteria"/>) and, for Patient on an API run, the request's own criteria
    /// (<paramref name="apiCriteria"/>) — the API winning a parameter both set, the same precedence a search run uses.
    /// Null when there is nothing to filter by.
    /// </summary>
    public static string? BulkExportCriteria(string resourceType, string? resourceCriteria, string? apiCriteria)
    {
        var api = string.Equals(resourceType, "Patient", StringComparison.OrdinalIgnoreCase)
            ? SplitParameters(apiCriteria).ToList()
            : [];
        var apiKeys = api.Select(ParameterKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combined = api
            .Concat(SplitParameters(resourceCriteria).Where(segment => !apiKeys.Contains(ParameterKey(segment))))
            .ToList();

        return combined.Count == 0 ? null : string.Join('&', combined);
    }

    private static IEnumerable<string> SplitParameters(string? criteria) =>
        string.IsNullOrWhiteSpace(criteria)
            ? []
            : criteria.Trim().TrimStart('?').Trim('&').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ParameterKey(string segment)
    {
        var equals = segment.IndexOf('=');
        return equals < 0 ? segment : segment[..equals];
    }

    private static List<CodeFilter> ParseFilters(string? searchParameters)
    {
        var filters = new List<CodeFilter>();
        if (string.IsNullOrWhiteSpace(searchParameters))
        {
            return filters;
        }

        foreach (var segment in SplitParameters(searchParameters))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = segment[..equals];
            var colon = key.IndexOf(':');
            var name = colon < 0 ? key : key[..colon];
            var modifier = colon < 0 ? null : key[(colon + 1)..];
            if (!EnforceableParameters.Contains(name) || (modifier is not null && !modifier.Equals("not", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Comma-separated values are OR'd; a token's optional "system|" prefix is irrelevant to a bare code element.
            var codes = Uri.UnescapeDataString(segment[(equals + 1)..])
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.Contains('|') ? value[(value.LastIndexOf('|') + 1)..] : value)
                .Where(code => code.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (codes.Count > 0)
            {
                // Every enforceable name is also its element's exact (lowercase) JSON property name.
                filters.Add(new CodeFilter(key, name.ToLowerInvariant(), Negated: modifier is not null, codes));
            }
        }

        return filters;
    }

    private static Dictionary<string, string> ReadTopLevelStrings(string rawJson, IReadOnlyList<CodeFilter> filters)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (var filter in filters)
            {
                if (document.RootElement.TryGetProperty(filter.Element, out var element) && element.ValueKind == JsonValueKind.String)
                {
                    values[filter.Element] = element.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // An unparseable record can't be evaluated — it reads as "element absent", same as a record without it.
        }

        return values;
    }

    /// <summary>One enforceable parameter: <paramref name="Parameter"/> as written (e.g. <c>status:not</c>), the
    /// JSON element it reads (<paramref name="Element"/>, the parameter name), and the codes it accepts.</summary>
    private sealed record CodeFilter(string Parameter, string Element, bool Negated, IReadOnlySet<string> Codes)
    {
        // A record without the element matches no code — so it fails a positive filter and passes a :not one,
        // which is how a server evaluates the same search.
        public bool Matches(string? value) => Negated
            ? value is null || !Codes.Contains(value)
            : value is not null && Codes.Contains(value);
    }
}
