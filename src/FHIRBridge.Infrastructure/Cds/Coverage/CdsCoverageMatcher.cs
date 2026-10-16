using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Cds.Handlers;

namespace FHIRBridge.Infrastructure.Cds.Coverage;

/// <summary>The payer a call's Coverage matched.</summary>
/// <param name="CoverageReference">"Coverage/&lt;id&gt;"; null when the Coverage has no id.</param>
internal sealed record CdsCoverageMatch(CdsPayer Payer, string? CoverageReference);

/// <summary>
/// Pure matching for coverage requirements: the active Coverages in prefetch "coverage", the payer one of them
/// belongs to (Coverage.payor identifier / display / referenced Organization, then plan values), and the payer rule
/// for an order's codes. Never throws on odd input.
/// </summary>
internal static class CdsCoverageMatcher
{
    public const string CoveragePrefetchKey = "coverage";

    private static readonly HashSet<string> InactiveStatuses = new(StringComparer.OrdinalIgnoreCase) { "cancelled", "draft", "entered-in-error" };

    /// <summary>The active Coverages (status active or not given), in Coverage.order (primary first).</summary>
    public static IReadOnlyList<JsonObject> ActiveCoverages(CdsRequest request) =>
        CdsFhirReader.Resources(request.Prefetch?[CoveragePrefetchKey], "Coverage")
            .Where(coverage => CdsFhirReader.Text(coverage["status"]) is not { } status || !InactiveStatuses.Contains(status))
            .Select((coverage, index) => (Coverage: coverage, Index: index, Order: CdsFhirReader.Number(coverage["order"])))
            .OrderBy(entry => entry.Order ?? double.MaxValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Coverage)
            .ToArray();

    /// <summary>The first Coverage (in order) whose payor and plan match an enabled payer.</summary>
    public static CdsCoverageMatch? MatchPayer(IReadOnlyList<JsonObject> coverages, JsonNode? coveragePrefetch, IReadOnlyList<CdsPayer> payers)
    {
        if (payers.Count == 0)
        {
            return null;
        }

        var organizations = CdsFhirReader.Resources(coveragePrefetch, "Organization");
        foreach (var coverage in coverages)
        {
            var candidates = PayorCandidates(coverage, organizations);
            var plans = (coverage["class"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(c => CdsFhirReader.Text(c["value"]))
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var payer in payers.Where(p => p.IsEnabled))
            {
                if (!payer.Identifiers.Any(identifier => Matches(identifier, candidates)))
                {
                    continue;
                }

                if (payer.PlanValues.Count > 0 && !payer.PlanValues.Any(plans.Contains))
                {
                    continue;
                }

                var id = CdsFhirReader.Text(coverage["id"]);
                return new CdsCoverageMatch(payer, string.IsNullOrEmpty(id) ? null : $"Coverage/{id}");
            }
        }

        return null;
    }

    /// <summary>
    /// The rule for an item's codes: an exact code match beats a prefix match, a longer prefix beats a shorter one,
    /// and between equals the most recent effective-from wins. Only rules effective on <paramref name="today"/>.
    /// </summary>
    public static CdsCoverageRule? MatchRule(IReadOnlyList<FhirCode> codes, IReadOnlyList<CdsCoverageRule> rules, DateOnly today)
    {
        CdsCoverageRule? best = null;
        (int Kind, int Length, DateOnly From) bestScore = default;
        foreach (var rule in rules)
        {
            if (!rule.IsEffectiveOn(today))
            {
                continue;
            }

            foreach (var code in codes)
            {
                if (code.Code is not { } value || code.System is not { } system || !SameSystem(system, rule.System))
                {
                    continue;
                }

                (int, int, DateOnly)? score = string.Equals(value.Trim(), rule.Code, StringComparison.OrdinalIgnoreCase)
                    ? (2, rule.Code.Length, rule.EffectiveFrom)
                    : rule.CodePrefixMatch && value.Trim().StartsWith(rule.Code, StringComparison.OrdinalIgnoreCase)
                        ? (1, rule.Code.Length, rule.EffectiveFrom)
                        : null;
                if (score is { } s && (best is null || s.CompareTo(bestScore) > 0))
                {
                    best = rule;
                    bestScore = s;
                }
            }
        }

        return best;
    }

    /// <summary>Code systems that have both a URL and an OID form match each other (see <see cref="CdsCoverageCodes.CodeSystems"/>).</summary>
    public static bool SameSystem(string left, string right) => CdsCoverageCodes.CodeSystems.Same(left, right);

    /// <summary>What a Coverage says about its payer: (system, value) identifiers and names.</summary>
    private static (List<(string? System, string Value)> Identifiers, List<string> Names) PayorCandidates(
        JsonObject coverage, IReadOnlyList<JsonObject> organizations)
    {
        var identifiers = new List<(string?, string)>();
        var names = new List<string>();
        foreach (var payor in (coverage["payor"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (CdsFhirReader.Text(payor["display"]) is { Length: > 0 } display)
            {
                names.Add(display.Trim());
            }

            AddIdentifier(payor["identifier"] as JsonObject, identifiers);
            if (Resolve(coverage, organizations, CdsFhirReader.Text(payor["reference"])) is { } organization)
            {
                if (CdsFhirReader.Text(organization["name"]) is { Length: > 0 } name)
                {
                    names.Add(name.Trim());
                }

                foreach (var alias in (organization["alias"] as JsonArray ?? []).Select(CdsFhirReader.Text).OfType<string>())
                {
                    names.Add(alias.Trim());
                }

                foreach (var identifier in (organization["identifier"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    AddIdentifier(identifier, identifiers);
                }
            }
        }

        return (identifiers, names);
    }

    private static void AddIdentifier(JsonObject? identifier, List<(string?, string)> identifiers)
    {
        if (CdsFhirReader.Text(identifier?["value"]) is { Length: > 0 } value)
        {
            identifiers.Add((CdsFhirReader.Text(identifier!["system"])?.Trim(), value.Trim()));
        }
    }

    private static JsonObject? Resolve(JsonObject coverage, IReadOnlyList<JsonObject> organizations, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (reference.StartsWith('#'))
        {
            return (coverage["contained"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(item => CdsFhirReader.ResourceType(item) == "Organization" && CdsFhirReader.Text(item["id"]) == reference[1..]);
        }

        var id = reference.TrimEnd('/');
        id = id[(id.LastIndexOf('/') + 1)..];
        return organizations.FirstOrDefault(org => CdsFhirReader.Text(org["id"]) == id);
    }

    /// <summary>"system|value" matches an identifier exactly; a bare entry matches an identifier value or a name.</summary>
    private static bool Matches(string entry, (List<(string? System, string Value)> Identifiers, List<string> Names) candidates)
    {
        var separator = entry.LastIndexOf('|');
        if (separator > 0)
        {
            var system = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            return candidates.Identifiers.Any(id =>
                id.System is not null
                && string.Equals(id.System, system, StringComparison.OrdinalIgnoreCase)
                && string.Equals(id.Value, value, StringComparison.OrdinalIgnoreCase));
        }

        var bare = entry.Trim();
        return candidates.Identifiers.Any(id => string.Equals(id.Value, bare, StringComparison.OrdinalIgnoreCase))
               || candidates.Names.Any(name => string.Equals(name, bare, StringComparison.OrdinalIgnoreCase));
    }
}
