using System.Globalization;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using static FHIRBridge.Infrastructure.Cds.Handlers.CdsSampleReferenceData;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// Care-gap reminders on patient-view: HbA1c overdue for a patient with diabetes, seasonal influenza vaccine
/// due, and colorectal cancer screening due in the configured age band. Intervals are settings; the code lists
/// are SAMPLE data in <see cref="CdsSampleReferenceData"/> (requires clinical review).
/// </summary>
public sealed class CareGapCdsHandler : CdsServiceHandlerBase
{
    private const string PatientKey = "patient";
    private const string ConditionsKey = "conditions";
    private const string Hba1cKey = "hba1c";
    private const string ImmunizationsKey = "immunizations";
    private const string ProceduresKey = "procedures";

    private static readonly IReadOnlyDictionary<string, string> PrefetchTemplates = new Dictionary<string, string>
    {
        [PatientKey] = "Patient/{{context.patientId}}",
        [ConditionsKey] = "Condition?patient={{context.patientId}}",
        [Hba1cKey] = "Observation?patient={{context.patientId}}&code=" +
                     string.Join(",", Hba1cLoincCodes.Select(code => $"{LoincSystem}|{code}")) + "&_sort=-date&_count=1",
        [ImmunizationsKey] = "Immunization?patient={{context.patientId}}",
        [ProceduresKey] = "Procedure?patient={{context.patientId}}",
    };

    /// <summary>Static topic codes, one per care gap (Epic ties a clinician's override to the code).</summary>
    private static readonly CdsCoding Hba1cTopic = CdsTopic.Create("CareGapHba1c", "HbA1c overdue");
    private static readonly CdsCoding InfluenzaTopic = CdsTopic.Create("CareGapInfluenza", "Influenza vaccine due");
    private static readonly CdsCoding ColorectalTopic = CdsTopic.Create("CareGapColorectal", "Colorectal cancer screening due");

    public override string Key => CdsHandlerKeys.CareGap;

    public override string DisplayName => "Care-gap reminders";

    public override string Description =>
        "Reminds the clinician of overdue care when the chart is opened: HbA1c for patients with diabetes, influenza vaccination and colorectal cancer screening (SAMPLE rules, requires clinical review).";

    public override IReadOnlyList<string> SupportedHooks { get; } = [CdsHookNames.PatientView];

    public override IReadOnlyList<CdsSettingDescriptor> Settings { get; } =
    [
        new("checkHba1c", "Check HbA1c for patients with diabetes", CdsSettingTypes.Boolean, JsonValue.Create(true)),
        new("hba1cIntervalMonths", "HbA1c interval (months)", CdsSettingTypes.Number, JsonValue.Create(6),
            "Remind when the latest HbA1c is older than this.", Min: 1, Max: 24),
        new("checkInfluenza", "Check influenza vaccination", CdsSettingTypes.Boolean, JsonValue.Create(true)),
        new("influenzaIntervalMonths", "Influenza vaccine interval (months)", CdsSettingTypes.Number, JsonValue.Create(12),
            "Remind when the latest influenza vaccine is older than this.", Min: 1, Max: 24),
        new("checkColorectalScreening", "Check colorectal cancer screening", CdsSettingTypes.Boolean, JsonValue.Create(true)),
        new("colorectalIntervalYears", "Colonoscopy interval (years)", CdsSettingTypes.Number, JsonValue.Create(10),
            "Remind when the latest colonoscopy is older than this.", Min: 1, Max: 15),
        new("colorectalAgeFrom", "Screening age from", CdsSettingTypes.Number, JsonValue.Create(45), Min: 18, Max: 100),
        new("colorectalAgeTo", "Screening age to", CdsSettingTypes.Number, JsonValue.Create(75), Min: 18, Max: 100),
        IndicatorSetting("indicator", "Card level", CdsIndicator.Info, "Indicator for every care-gap card."),
    ];

    /// <summary>Fix mappings are keyed by the care gap (<see cref="CareGapTriggers"/>).</summary>
    public override IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes { get; } =
    [
        CdsFixTypeCatalog.AddTestOrder(CareGapTriggers),
        CdsFixTypeCatalog.AddProblem(CareGapTriggers),
        CdsFixTypeCatalog.AddEncounterDiagnosis(CareGapTriggers),
    ];

    /// <summary>The care gaps a fix mapping can be keyed by (also the cards' topic codes' basis).</summary>
    public static IReadOnlyList<CdsSettingOption> CareGapTriggers { get; } =
    [
        new("hba1c", "HbA1c test"),
        new("influenza", "Influenza vaccination"),
        new("colorectal", "Colorectal cancer screening"),
    ];

    public override IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => PrefetchTemplates;

    public override CdsRequest BuildSampleRequest(string hook)
    {
        var patient = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = SamplePatientId,
            ["birthDate"] = DateTime.UtcNow.Date.AddYears(-60).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["gender"] = "female",
        };
        var diabetes = new JsonObject
        {
            ["resourceType"] = "Condition",
            ["id"] = "sample-condition-1",
            ["clinicalStatus"] = Concept("http://terminology.hl7.org/CodeSystem/condition-clinical", "active", "Active"),
            ["code"] = Concept(SnomedSystem, "44054006", "Diabetes mellitus type 2"),
            ["subject"] = PatientReference(),
        };
        var hba1c = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = "sample-hba1c-1",
            ["status"] = "final",
            ["code"] = Concept(LoincSystem, "4548-4", "Hemoglobin A1c/Hemoglobin.total in Blood"),
            ["subject"] = PatientReference(),
            ["effectiveDateTime"] = SampleDate(300),
            ["valueQuantity"] = new JsonObject { ["value"] = 7.9, ["unit"] = "%" },
        };
        var flu = new JsonObject
        {
            ["resourceType"] = "Immunization",
            ["id"] = "sample-immunization-1",
            ["status"] = "completed",
            ["vaccineCode"] = Concept(CvxSystem, "158", "influenza, injectable, quadrivalent"),
            ["patient"] = PatientReference(),
            ["occurrenceDateTime"] = SampleDate(420),
        };

        return SampleRequest(
            hook,
            [],
            new JsonObject
            {
                [PatientKey] = patient,
                [ConditionsKey] = SearchSet(diabetes),
                [Hba1cKey] = SearchSet(hba1c),
                [ImmunizationsKey] = SearchSet(flu),
                [ProceduresKey] = SearchSet(),
            });
    }

    protected override CdsHandlerResult Evaluate(CdsHandlerContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var indicator = settings.GetText("indicator", CdsIndicator.Info)!;
        var now = DateTimeOffset.UtcNow;
        var cards = new List<CdsCard>();

        if (settings.GetBoolean("checkHba1c", true) && HasDiabetes(Prefetch(context, ConditionsKey)))
        {
            var months = (int)settings.GetNumber("hba1cIntervalMonths", 6);
            var last = LatestDate(Prefetch(context, Hba1cKey), "Observation", resource => CdsFhirReader.HasCode(resource["code"], Hba1cLoincCodes));
            if (last is null || last < now.AddMonths(-months))
            {
                cards.Add(context.CreateCard(
                    $"HbA1c overdue: {Describe(last, "no result on record")}",
                    indicator,
                    Detail(
                        $"The patient has diabetes on the problem list and no HbA1c in the last **{months} months** " +
                        $"(latest: {DateText(last)}).",
                        "Consider ordering an HbA1c."),
                    CardLinks(context),
                    context.OverrideReasons,
                    Hba1cTopic,
                    GapFixes(context, "hba1c")));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (settings.GetBoolean("checkInfluenza", true))
        {
            var months = (int)settings.GetNumber("influenzaIntervalMonths", 12);
            var last = LatestDate(Prefetch(context, ImmunizationsKey), "Immunization", IsInfluenzaVaccine);
            if (last is null || last < now.AddMonths(-months))
            {
                cards.Add(context.CreateCard(
                    $"Influenza vaccine due: {Describe(last, "none on record")}",
                    indicator,
                    Detail(
                        $"No influenza vaccination is recorded in the last **{months} months** (latest: {DateText(last)}).",
                        "Offer the seasonal influenza vaccine if not contraindicated."),
                    CardLinks(context),
                    context.OverrideReasons,
                    InfluenzaTopic,
                    GapFixes(context, "influenza")));
            }
        }

        if (settings.GetBoolean("checkColorectalScreening", true)
            && Age(Prefetch(context, PatientKey), now) is { } age
            && age >= settings.GetNumber("colorectalAgeFrom", 45)
            && age <= settings.GetNumber("colorectalAgeTo", 75))
        {
            var years = (int)settings.GetNumber("colorectalIntervalYears", 10);
            var last = LatestDate(Prefetch(context, ProceduresKey), "Procedure", IsColonoscopy);
            if (last is null || last < now.AddYears(-years))
            {
                cards.Add(context.CreateCard(
                    $"Colorectal cancer screening due: {Describe(last, "no colonoscopy on record")}",
                    indicator,
                    Detail(
                        $"The patient is in the screening age band and has no colonoscopy in the last **{years} years** " +
                        $"(latest: {DateText(last)}).",
                        "Discuss colorectal cancer screening options."),
                    CardLinks(context),
                    context.OverrideReasons,
                    ColorectalTopic,
                    GapFixes(context, "colorectal")));
            }
        }

        return new CdsHandlerResult(cards);
    }

    /// <summary>Order the mapped test (the care-gap key is the trigger), then the diagnosis fixes for the same gap.</summary>
    private static IEnumerable<CdsSuggestion?> GapFixes(CdsHandlerContext context, string gap)
    {
        var suggestions = new List<CdsSuggestion?>();
        if (PatientId(context) is { } patientId && FindMapping(context, CdsFixTypeKeys.AddTestOrder, [gap]) is { } test)
        {
            suggestions.Add(context.CreateCreateSuggestion(
                CdsFixTypeKeys.AddTestOrder,
                $"Order {MappingName(test)}",
                CdsFixResources.ServiceRequest(test, patientId),
                $"Adds an unsigned order for {MappingName(test)}."));
        }

        suggestions.AddRange(DiagnosisFixes(context, [gap]));
        return suggestions;
    }

    private static bool HasDiabetes(JsonNode? conditions) =>
        CdsFhirReader.Resources(conditions, "Condition")
            .Where(condition => CdsFhirReader.StatusCode(condition["clinicalStatus"]) is null or "active" or "recurrence" or "relapse")
            .Where(condition => CdsFhirReader.StatusCode(condition["verificationStatus"]) is not ("refuted" or "entered-in-error"))
            .Any(condition =>
                CdsFhirReader.HasCode(condition["code"], DiabetesSnomedCodes)
                || CdsFhirReader.Codings(condition["code"]).Any(coding =>
                    coding.Code is not null
                    && DiabetesIcd10Prefixes.Any(prefix => coding.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                || DiabetesTerms.Any(term => CdsFhirReader.ContainsTerm(CdsFhirReader.SearchText(condition["code"]), term)));

    private static bool IsInfluenzaVaccine(JsonObject immunization) =>
        CdsFhirReader.Text(immunization["status"]) is null or "completed"
        && (CdsFhirReader.HasCode(immunization["vaccineCode"], InfluenzaCvxCodes)
            || InfluenzaTerms.Any(term => CdsFhirReader.ContainsTerm(CdsFhirReader.SearchText(immunization["vaccineCode"]), term)));

    private static bool IsColonoscopy(JsonObject procedure) =>
        CdsFhirReader.Text(procedure["status"]) is null or "completed"
        && (CdsFhirReader.HasCode(procedure["code"], ColonoscopySnomedCodes)
            || ColonoscopyTerms.Any(term => CdsFhirReader.ContainsTerm(CdsFhirReader.SearchText(procedure["code"]), term)));

    private static DateTimeOffset? LatestDate(JsonNode? node, string resourceType, Func<JsonObject, bool> predicate) =>
        CdsFhirReader.Resources(node, resourceType)
            .Where(resource => CdsFhirReader.Text(resource["status"]) is not ("entered-in-error" or "cancelled" or "not-done"))
            .Where(predicate)
            .Select(CdsFhirReader.ClinicalDate)
            .Where(date => date is not null)
            .Max();

    private static int? Age(JsonNode? patientNode, DateTimeOffset now)
    {
        var patient = CdsFhirReader.Resources(patientNode, "Patient").FirstOrDefault();
        if (patient is null || CdsFhirReader.Date(patient["birthDate"]) is not { } birthDate)
        {
            return null;
        }

        var age = now.Year - birthDate.Year;
        if (now.UtcDateTime.Date < birthDate.UtcDateTime.Date.AddYears(age))
        {
            age--;
        }

        return age;
    }

    private static string DateText(DateTimeOffset? date) => date is { } value ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "none found";

    private static string Describe(DateTimeOffset? date, string none) =>
        date is { } value ? $"last on {value:yyyy-MM-dd}" : none;
}
