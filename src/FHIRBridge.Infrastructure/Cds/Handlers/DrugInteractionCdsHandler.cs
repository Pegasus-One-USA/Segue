using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using static FHIRBridge.Infrastructure.Cds.Handlers.CdsSampleReferenceData;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// Drug–drug interaction check: compares each drafted medication with the patient's active medications
/// (prefetch "medications") and, optionally, with the other drafted orders. Uses the SAMPLE interaction table in
/// <see cref="CdsSampleReferenceData"/> (requires clinical review).
/// </summary>
public sealed class DrugInteractionCdsHandler : CdsServiceHandlerBase
{
    private const string MedicationsKey = "medications";

    private static readonly IReadOnlyDictionary<string, string> PrefetchTemplates = new Dictionary<string, string>
    {
        // _include brings the referenced Medication resources (Epic sends medicationReference since Feb 2024), so
        // active meds are matched by their RxNorm code, not only by display text.
        [MedicationsKey] = "MedicationRequest?patient={{context.patientId}}&status=active&_include=MedicationRequest:medication",
    };

    private static readonly HashSet<string> InactiveStatuses =
        new(["stopped", "cancelled", "completed", "entered-in-error"], StringComparer.Ordinal);

    /// <summary>Static topic codes, one per severity (Epic ties a clinician's override to the code).</summary>
    private static readonly IReadOnlyDictionary<string, CdsCoding> SeverityTopics = new Dictionary<string, CdsCoding>(StringComparer.Ordinal)
    {
        [InteractionSeverity.Contraindicated] = CdsTopic.Create("DrugInteractionContraindicated", "Contraindicated drug interaction"),
        [InteractionSeverity.Major] = CdsTopic.Create("DrugInteractionMajor", "Major drug interaction"),
        [InteractionSeverity.Moderate] = CdsTopic.Create("DrugInteractionModerate", "Moderate drug interaction"),
    };

    private static readonly CdsCoding InteractionTopic = CdsTopic.Create("DrugInteraction", "Drug interaction");

    public override string Key => CdsHandlerKeys.DrugInteraction;

    public override string DisplayName => "Drug–drug interaction check";

    public override string Description =>
        "Warns when a drafted medication interacts with one of the patient's active medications or another drafted order (SAMPLE rules, requires clinical review).";

    public override IReadOnlyList<string> SupportedHooks { get; } = [CdsHookNames.OrderSelect, CdsHookNames.OrderSign];

    public override IReadOnlyList<CdsSettingDescriptor> Settings { get; } =
    [
        new("minimumSeverity", "Minimum severity to show", CdsSettingTypes.Select, JsonValue.Create(InteractionSeverity.Moderate),
            "Interactions below this severity produce no card.",
            [
                new(InteractionSeverity.Moderate, "Moderate and above"),
                new(InteractionSeverity.Major, "Major and above"),
                new(InteractionSeverity.Contraindicated, "Contraindicated only"),
            ]),
        new("checkWithinDraftOrders", "Check drafted orders against each other", CdsSettingTypes.Boolean, JsonValue.Create(true),
            "Also check pairs of orders drafted together, not only drafted orders against active medications."),
        IndicatorSetting("contraindicatedIndicator", "Card level: contraindicated", CdsIndicator.Critical, "Indicator for a contraindicated combination."),
        IndicatorSetting("majorIndicator", "Card level: major", CdsIndicator.Warning, "Indicator for a major interaction."),
        IndicatorSetting("moderateIndicator", "Card level: moderate", CdsIndicator.Info, "Indicator for a moderate interaction."),
    ];

    /// <summary>Fix mappings are keyed by the ingredient of the draft order being replaced (e.g. "ibuprofen").</summary>
    public override IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes { get; } =
    [
        CdsFixTypeCatalog.RemoveDraftOrder(),
        CdsFixTypeCatalog.AddAlternativeMedication(IngredientTriggers(CdsSampleReferenceData.Ingredients.Select(i => i.Name))),
        CdsFixTypeCatalog.AddProblem(null),
        CdsFixTypeCatalog.AddEncounterDiagnosis(null),
    ];

    public override IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => PrefetchTemplates;

    public override CdsRequest BuildSampleRequest(string hook) =>
        SampleRequest(
            hook,
            [SampleMedicationRequest("draft-ibuprofen", "draft", "197805", "ibuprofen 400 MG Oral Tablet")],
            new JsonObject
            {
                [MedicationsKey] = SearchSet(
                    SampleMedicationRequest("active-warfarin", "active", "855332", "warfarin sodium 5 MG Oral Tablet")),
            });

    protected override CdsHandlerResult Evaluate(CdsHandlerContext context, CancellationToken cancellationToken)
    {
        var medicationsNode = Prefetch(context, MedicationsKey);
        if (medicationsNode is null)
        {
            return new CdsHandlerResult([], "The active medication list was not provided.");
        }

        var minimumRank = InteractionSeverity.Rank(context.Settings.GetText("minimumSeverity", InteractionSeverity.Moderate)!);
        var medications = Medications(context);
        var drafts = DraftOrders(context).Select((order, index) => Describe(order, $"draft#{index}", medications)).ToArray();
        var active = CdsFhirReader.Resources(medicationsNode, "MedicationRequest")
            .Where(med => !InactiveStatuses.Contains(CdsFhirReader.Text(med["status"]) ?? string.Empty))
            .Select((med, index) => Describe(med, $"active#{index}", medications))
            .ToArray();

        var others = context.Settings.GetBoolean("checkWithinDraftOrders", true) ? active.Concat(drafts).ToArray() : active;
        var selected = OrdersToCheck(context);
        var toCheck = drafts.Where(draft => selected.Any(order => ReferenceEquals(order, draft.Resource))).ToArray();

        var seenPairs = new HashSet<string>(StringComparer.Ordinal);
        var cards = new List<CdsCard>();
        foreach (var order in toCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var other in others)
            {
                if (other.Identity == order.Identity)
                {
                    continue;
                }

                var rules = Interactions
                    .Where(rule => order.Ingredients.Any(x => other.Ingredients.Any(y => x != y && Pairs(rule, x, y))))
                    .Where(rule => InteractionSeverity.Rank(rule.Severity) >= minimumRank)
                    .OrderByDescending(rule => InteractionSeverity.Rank(rule.Severity))
                    .ToArray();
                if (rules.Length == 0)
                {
                    continue;
                }

                var pairKey = string.CompareOrdinal(order.Identity, other.Identity) < 0
                    ? $"{order.Identity}|{other.Identity}"
                    : $"{other.Identity}|{order.Identity}";
                if (!seenPairs.Add(pairKey))
                {
                    continue;
                }

                var severity = rules[0].Severity;
                var otherKind = other.Identity.StartsWith("draft", StringComparison.Ordinal) ? "drafted order" : "active medication";
                cards.Add(context.CreateCard(
                    $"{Capitalize(severity)} interaction: {order.Label} + {other.Label}",
                    context.Settings.GetText($"{severity}Indicator", CdsIndicator.Warning)!,
                    Detail(
                        $"**{order.Label}** interacts with the {otherKind} **{other.Label}** (severity: {severity}).",
                        string.Join("\n", rules.Select(rule => $"- {rule.Effect} {rule.Advice}"))),
                    CardLinks(context),
                    context.OverrideReasons,
                    SeverityTopics.GetValueOrDefault(severity, InteractionTopic),
                    Fixes(context, order, other)));
            }
        }

        return new CdsHandlerResult(cards);
    }

    /// <summary>
    /// Remove the checked draft order and order the alternative mapped for one of its ingredients; when the other
    /// drug is a draft too, removing it instead is offered as well. Diagnosis fixes use the catch-all mapping.
    /// </summary>
    private static IEnumerable<CdsSuggestion?> Fixes(CdsHandlerContext context, Medication order, Medication other)
    {
        var suggestions = MedicationOrderFixes(
            context,
            order.Resource,
            order.Label,
            order.Ingredients.Select(i => i.Name),
            null).ToList();

        if (other.Identity.StartsWith("draft", StringComparison.Ordinal)
            && CdsFhirReader.ResourceType(other.Resource) == "MedicationRequest"
            && OrderReference(other.Resource) is { } otherReference)
        {
            // Right after "remove this order" (when present), before the alternative. Never recommended: removing
            // either drug resolves the interaction, so pre-selecting both would delete both orders.
            var removeOther = context.CreateDeleteSuggestion(
                CdsFixTypeKeys.RemoveDraftOrder,
                $"Remove {other.Label}",
                otherReference,
                $"Removes the unsigned order for {other.Label}.");
            if (removeOther is not null)
            {
                removeOther.IsRecommended = null;
            }

            suggestions.Insert(Math.Min(1, suggestions.Count), removeOther);
        }

        return suggestions;
    }

    private static bool Pairs(Interaction rule, Ingredient x, Ingredient y) =>
        (CdsDrugMatcher.Matches(x, rule.A) && CdsDrugMatcher.Matches(y, rule.B))
        || (CdsDrugMatcher.Matches(x, rule.B) && CdsDrugMatcher.Matches(y, rule.A));

    private static Medication Describe(JsonObject resource, string fallbackIdentity, CdsMedicationIndex medications)
    {
        var concept = CdsFhirReader.OrderConcept(resource, medications);
        var id = CdsFhirReader.Text(resource["id"]);
        var prefix = fallbackIdentity.StartsWith("draft", StringComparison.Ordinal) ? "draft" : "active";
        return new Medication(
            resource,
            id is null ? fallbackIdentity : $"{prefix}:{CdsFhirReader.ResourceType(resource)}/{id}",
            CdsFhirReader.Label(concept, "a medication"),
            CdsDrugMatcher.IngredientsOf(concept));
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record Medication(JsonObject Resource, string Identity, string Label, IReadOnlyList<Ingredient> Ingredients);
}
