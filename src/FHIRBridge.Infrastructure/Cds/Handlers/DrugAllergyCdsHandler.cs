using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// Drug–allergy check: compares each drafted medication with the patient's recorded allergies (prefetch
/// "allergies") by ingredient and by drug class, plus optional class cross-reactivity (e.g. penicillin →
/// cephalosporin). Uses <see cref="CdsSampleReferenceData"/> (SAMPLE data, requires clinical review).
/// </summary>
public sealed class DrugAllergyCdsHandler : CdsServiceHandlerBase
{
    private const string AllergiesKey = "allergies";

    private static readonly IReadOnlyDictionary<string, string> PrefetchTemplates = new Dictionary<string, string>
    {
        [AllergiesKey] = "AllergyIntolerance?patient={{context.patientId}}",
    };

    /// <summary>Static topic codes (Epic ties a clinician's override to the code), one per rule.</summary>
    private static readonly CdsCoding AllergyMatchTopic = CdsTopic.Create("DrugAllergyMatch", "Drug-allergy match");
    private static readonly CdsCoding CrossReactivityTopic = CdsTopic.Create("DrugAllergyCrossReactivity", "Drug-allergy cross-reactivity");

    public override string Key => CdsHandlerKeys.DrugAllergy;

    public override string DisplayName => "Drug–allergy check";

    public override string Description =>
        "Warns when a drafted medication matches one of the patient's recorded drug allergies, by ingredient or drug class (SAMPLE rules, requires clinical review).";

    public override IReadOnlyList<string> SupportedHooks { get; } = [CdsHookNames.OrderSelect, CdsHookNames.OrderSign];

    public override IReadOnlyList<CdsSettingDescriptor> Settings { get; } =
    [
        IndicatorSetting("allergyIndicator", "Card level for an allergy match", CdsIndicator.Warning,
            "Indicator when the drafted drug, or its class, matches a recorded allergy."),
        IndicatorSetting("highCriticalityIndicator", "Card level when the allergy is high criticality", CdsIndicator.Critical,
            "Used instead of the level above when the allergy's criticality is \"high\"."),
        new("checkCrossReactivity", "Check class cross-reactivity", CdsSettingTypes.Boolean, JsonValue.Create(true),
            "Also warn on known cross-reactive classes (e.g. a penicillin allergy and a cephalosporin order)."),
        IndicatorSetting("crossReactivityIndicator", "Card level for cross-reactivity", CdsIndicator.Info,
            "Indicator for a cross-reactivity card."),
        new("includeInactiveAllergies", "Include inactive or resolved allergies", CdsSettingTypes.Boolean, JsonValue.Create(false),
            "By default only allergies whose clinical status is active (or not recorded) are checked."),
    ];

    /// <summary>Fix mappings are keyed by the allergy's drug class (e.g. "penicillin").</summary>
    public override IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes { get; } =
    [
        CdsFixTypeCatalog.RemoveDraftOrder(),
        CdsFixTypeCatalog.AddAlternativeMedication(DrugClassTriggers),
        CdsFixTypeCatalog.AddProblem(DrugClassTriggers),
        CdsFixTypeCatalog.AddEncounterDiagnosis(DrugClassTriggers),
    ];

    public override IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => PrefetchTemplates;

    public override CdsRequest BuildSampleRequest(string hook)
    {
        var allergy = new JsonObject
        {
            ["resourceType"] = "AllergyIntolerance",
            ["id"] = "sample-allergy-1",
            ["clinicalStatus"] = Concept("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", "active", "Active"),
            ["verificationStatus"] = Concept("http://terminology.hl7.org/CodeSystem/allergyintolerance-verification", "confirmed", "Confirmed"),
            ["category"] = new JsonArray("medication"),
            ["criticality"] = "high",
            ["code"] = Concept(CdsSampleReferenceData.SnomedSystem, "91936005", "Allergy to penicillin"),
            ["patient"] = PatientReference(),
        };

        return SampleRequest(
            hook,
            [SampleMedicationRequest("draft-amoxicillin", "draft", "308191", "amoxicillin 500 MG Oral Capsule")],
            new JsonObject { [AllergiesKey] = SearchSet(allergy) });
    }

    protected override CdsHandlerResult Evaluate(CdsHandlerContext context, CancellationToken cancellationToken)
    {
        var allergiesNode = Prefetch(context, AllergiesKey);
        if (allergiesNode is null)
        {
            return new CdsHandlerResult([], "The allergy list was not provided.");
        }

        var includeInactive = context.Settings.GetBoolean("includeInactiveAllergies");
        var checkCrossReactivity = context.Settings.GetBoolean("checkCrossReactivity", true);
        var allergies = CdsFhirReader.Resources(allergiesNode, "AllergyIntolerance")
            .Where(allergy => IsRelevant(allergy, includeInactive))
            .Select(allergy =>
            {
                var ingredients = CdsDrugMatcher.IngredientsOf(allergy["code"]);
                return new
                {
                    Label = CdsFhirReader.Label(allergy["code"], "a recorded allergy"),
                    IsHighCriticality = CdsFhirReader.Text(allergy["criticality"]) == "high",
                    Ingredients = ingredients,
                    Classes = CdsDrugMatcher.ClassesOfAllergen(allergy["code"], ingredients),
                };
            })
            .Where(allergy => allergy.Classes.Count > 0 || allergy.Ingredients.Count > 0)
            .ToArray();

        var medications = Medications(context);
        var cards = new List<CdsCard>();
        foreach (var order in OrdersToCheck(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concept = CdsFhirReader.OrderConcept(order, medications);
            var orderIngredients = CdsDrugMatcher.IngredientsOf(concept);
            if (orderIngredients.Count == 0)
            {
                continue;
            }

            var orderLabel = CdsFhirReader.Label(concept, "the drafted medication");
            var orderClasses = orderIngredients.SelectMany(i => i.Classes).ToHashSet(StringComparer.Ordinal);

            foreach (var allergy in allergies)
            {
                var sameDrug = orderIngredients.Intersect(allergy.Ingredients).Select(i => i.Name).ToArray();
                var sameClass = orderClasses.Where(allergy.Classes.Contains).ToArray();
                if (sameDrug.Length > 0 || sameClass.Length > 0)
                {
                    var match = sameDrug.Length > 0
                        ? $"Same drug: **{string.Join(", ", sameDrug)}**."
                        : $"Same drug class: **{string.Join(", ", sameClass.Select(CdsDrugMatcher.ClassName))}**.";
                    cards.Add(context.CreateCard(
                        $"Allergy alert: {orderLabel} — recorded allergy: {allergy.Label}",
                        context.Settings.GetText(allergy.IsHighCriticality ? "highCriticalityIndicator" : "allergyIndicator", CdsIndicator.Warning)!,
                        Detail(
                            $"The drafted order **{orderLabel}** matches the recorded allergy **{allergy.Label}**" +
                            (allergy.IsHighCriticality ? " (criticality: high)." : "."),
                            match,
                            "Confirm the allergy history and consider an alternative agent."),
                        CardLinks(context),
                        context.OverrideReasons,
                        AllergyMatchTopic,
                        MedicationOrderFixes(context, order, orderLabel, FixTriggers(sameClass, allergy.Classes), FixTriggers(sameClass, allergy.Classes))));
                    continue;
                }

                if (!checkCrossReactivity)
                {
                    continue;
                }

                var crossReactive = CdsSampleReferenceData.CrossReactivities
                    .Where(rule => allergy.Classes.Contains(rule.AllergyClass) && orderClasses.Contains(rule.OrderClass))
                    .ToArray();
                if (crossReactive.Length > 0)
                {
                    cards.Add(context.CreateCard(
                        $"Possible cross-reactivity: {orderLabel} — recorded allergy: {allergy.Label}",
                        context.Settings.GetText("crossReactivityIndicator", CdsIndicator.Info)!,
                        Detail(
                            $"**{orderLabel}** ({string.Join(", ", crossReactive.Select(r => CdsDrugMatcher.ClassName(r.OrderClass)))}) " +
                            $"may cross-react with the recorded allergy **{allergy.Label}** " +
                            $"({string.Join(", ", crossReactive.Select(r => CdsDrugMatcher.ClassName(r.AllergyClass)))}).",
                            string.Join(" ", crossReactive.Select(r => r.Note))),
                        CardLinks(context),
                        context.OverrideReasons,
                        CrossReactivityTopic,
                        MedicationOrderFixes(context, order, orderLabel, FixTriggers([], allergy.Classes), FixTriggers([], allergy.Classes))));
                }
            }
        }

        return new CdsHandlerResult(cards);
    }

    /// <summary>Fix-mapping triggers (drug class keys): the matched classes first, then the allergy's other classes.</summary>
    private static string[] FixTriggers(IEnumerable<string> matchedClasses, IEnumerable<string> allergyClasses) =>
        matchedClasses.Concat(allergyClasses.Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();

    private static bool IsRelevant(JsonObject allergy, bool includeInactive)
    {
        var verification = CdsFhirReader.StatusCode(allergy["verificationStatus"]);
        if (verification is "entered-in-error" or "refuted")
        {
            return false;
        }

        var clinical = CdsFhirReader.StatusCode(allergy["clinicalStatus"]);
        return includeInactive || clinical is null or "active";
    }
}
