using System.Globalization;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using static FHIRBridge.Infrastructure.Cds.Handlers.CdsSampleReferenceData;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// Renal dosing check: reads the patient's latest eGFR (prefetch "egfr") and flags drafted renally cleared
/// drugs from the SAMPLE <see cref="CdsSampleReferenceData.RenalRules"/> (requires clinical review).
/// </summary>
public sealed class RenalDoseCdsHandler : CdsServiceHandlerBase
{
    private const string EgfrKey = "egfr";

    private static readonly IReadOnlyDictionary<string, string> PrefetchTemplates = new Dictionary<string, string>
    {
        [EgfrKey] = "Observation?patient={{context.patientId}}&code=" +
                    string.Join(",", new[] { "33914-3", "48642-3", "62238-1", "98979-8" }.Select(code => $"{LoincSystem}|{code}")) +
                    "&_sort=-date&_count=1",
    };

    /// <summary>Static topic codes (Epic ties a clinician's override to the code), one per rule outcome.</summary>
    private static readonly CdsCoding DoseReviewTopic = CdsTopic.Create("RenalDoseReview", "Renal dose review");
    private static readonly CdsCoding AvoidTopic = CdsTopic.Create("RenalDoseAvoid", "Avoid at current renal function");

    public override string Key => CdsHandlerKeys.RenalDose;

    public override string DisplayName => "Renal dose check";

    public override string Description =>
        "Flags drafted renally cleared drugs that need a dose review or should be avoided at the patient's latest eGFR (SAMPLE rules, requires clinical review).";

    public override IReadOnlyList<string> SupportedHooks { get; } = [CdsHookNames.OrderSelect, CdsHookNames.OrderSign];

    public override IReadOnlyList<CdsSettingDescriptor> Settings { get; } =
    [
        new("egfrThreshold", "eGFR threshold (mL/min/1.73m²)", CdsSettingTypes.Number, JsonValue.Create(60),
            "Alert only when the latest eGFR is below this value.", Min: 5, Max: 120),
        new("useDrugThresholds", "Use each drug's own threshold", CdsSettingTypes.Boolean, JsonValue.Create(true),
            "On: a drug alerts only below both this threshold and its own sample review threshold. Off: every listed drug alerts below the threshold above."),
        new("maxResultAgeDays", "Ignore eGFR results older than (days)", CdsSettingTypes.Number, JsonValue.Create(365),
            "An older result is treated as missing.", Min: 1, Max: 3650),
        IndicatorSetting("indicator", "Card level: dose review", CdsIndicator.Warning, "Indicator when the dose should be reviewed."),
        IndicatorSetting("avoidIndicator", "Card level: avoid", CdsIndicator.Critical, "Indicator when the eGFR is below the drug's avoid threshold."),
    ];

    /// <summary>Fix mappings are keyed by the renally-cleared ingredient (e.g. "metformin").</summary>
    public override IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes { get; } =
    [
        CdsFixTypeCatalog.RemoveDraftOrder(),
        CdsFixTypeCatalog.AddAlternativeMedication(IngredientTriggers(CdsSampleReferenceData.RenalRules.Select(r => r.Ingredient))),
        CdsFixTypeCatalog.AddProblem(null),
        CdsFixTypeCatalog.AddEncounterDiagnosis(null),
    ];

    public override IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => PrefetchTemplates;

    public override CdsRequest BuildSampleRequest(string hook)
    {
        var egfr = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = "sample-egfr-1",
            ["status"] = "final",
            ["code"] = Concept(LoincSystem, "62238-1", "Glomerular filtration rate/1.73 sq M.predicted [Volume Rate/Area] in Serum, Plasma or Blood by Creatinine-based formula (CKD-EPI)"),
            ["subject"] = PatientReference(),
            ["effectiveDateTime"] = SampleDate(10),
            ["valueQuantity"] = new JsonObject
            {
                ["value"] = 25,
                ["unit"] = "mL/min/{1.73_m2}",
                ["system"] = "http://unitsofmeasure.org",
                ["code"] = "mL/min/{1.73_m2}",
            },
        };

        return SampleRequest(
            hook,
            [SampleMedicationRequest("draft-metformin", "draft", "861007", "metformin hydrochloride 500 MG Oral Tablet")],
            new JsonObject { [EgfrKey] = SearchSet(egfr) });
    }

    protected override CdsHandlerResult Evaluate(CdsHandlerContext context, CancellationToken cancellationToken)
    {
        var latest = CdsFhirReader.Resources(Prefetch(context, EgfrKey), "Observation")
            .Where(observation => CdsFhirReader.Text(observation["status"]) is not ("entered-in-error" or "cancelled"))
            .Where(observation => CdsFhirReader.HasCode(observation["code"], EgfrLoincCodes))
            .Select(observation => new
            {
                Value = CdsFhirReader.Number((observation["valueQuantity"] as JsonObject)?["value"]),
                Date = CdsFhirReader.ClinicalDate(observation),
            })
            .Where(result => result.Value is not null)
            .OrderByDescending(result => result.Date ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (latest is null)
        {
            return new CdsHandlerResult([], "No eGFR result was provided.");
        }

        var maxAgeDays = context.Settings.GetNumber("maxResultAgeDays", 365);
        if (latest.Date is { } date && date < DateTimeOffset.UtcNow.AddDays(-maxAgeDays))
        {
            return new CdsHandlerResult([], $"The latest eGFR is older than {maxAgeDays.ToString(CultureInfo.InvariantCulture)} days.");
        }

        var egfr = latest.Value!.Value;
        var threshold = context.Settings.GetNumber("egfrThreshold", 60);
        var useDrugThresholds = context.Settings.GetBoolean("useDrugThresholds", true);
        var egfrText = egfr.ToString("0.#", CultureInfo.InvariantCulture);
        var resultDate = latest.Date is { } when ? $" on {when:yyyy-MM-dd}" : string.Empty;

        var medications = Medications(context);
        var cards = new List<CdsCard>();
        foreach (var order in OrdersToCheck(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concept = CdsFhirReader.OrderConcept(order, medications);
            var label = CdsFhirReader.Label(concept, "the drafted medication");
            var ingredients = CdsDrugMatcher.IngredientsOf(concept).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var rule in RenalRules.Where(rule => ingredients.Contains(rule.Ingredient)))
            {
                var limit = useDrugThresholds ? Math.Min(threshold, rule.ReviewBelow) : threshold;
                if (egfr >= limit)
                {
                    continue;
                }

                var avoid = rule.AvoidBelow is { } avoidBelow && egfr < avoidBelow;
                cards.Add(context.CreateCard(
                    avoid
                        ? $"Avoid {label}: eGFR {egfrText} is below {rule.AvoidBelow!.Value.ToString("0.#", CultureInfo.InvariantCulture)}"
                        : $"Renal dose review: {label} (eGFR {egfrText})",
                    context.Settings.GetText(avoid ? "avoidIndicator" : "indicator", CdsIndicator.Warning)!,
                    Detail(
                        $"Latest eGFR: **{egfrText} mL/min/1.73m²**{resultDate}.",
                        $"**{rule.Ingredient}**: {rule.Advice}"),
                    CardLinks(context),
                    context.OverrideReasons,
                    avoid ? AvoidTopic : DoseReviewTopic,
                    MedicationOrderFixes(context, order, label, [rule.Ingredient], null)));
            }
        }

        return new CdsHandlerResult(cards);
    }
}
