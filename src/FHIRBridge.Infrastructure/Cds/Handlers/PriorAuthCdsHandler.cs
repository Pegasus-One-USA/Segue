using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using static FHIRBridge.Infrastructure.Cds.Handlers.CdsSampleReferenceData;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// Prior-authorization reminder on order-sign: flags any drafted order (service, medication or device) whose code
/// is on the instance's configured list. The default list is SAMPLE data (requires review against the actual
/// payer rules). Uses the context alone — no prefetch.
/// </summary>
public sealed class PriorAuthCdsHandler : CdsServiceHandlerBase
{
    /// <summary>Static topic code (Epic ties a clinician's override to the code).</summary>
    private static readonly CdsCoding PriorAuthTopic = CdsTopic.Create("PriorAuthRequired", "Prior authorization required");

    public override string Key => CdsHandlerKeys.PriorAuth;

    public override string DisplayName => "Prior-authorization reminder";

    public override string Description =>
        "Reminds the clinician at signing when a drafted order is on the configured list of codes that require prior authorization (SAMPLE list, review against payer rules).";

    public override IReadOnlyList<string> SupportedHooks { get; } = [CdsHookNames.OrderSign];

    public override IReadOnlyList<CdsSettingDescriptor> Settings { get; } =
    [
        new("codes", "Codes that require prior authorization", CdsSettingTypes.List,
            new JsonArray(PriorAuthDefaultCodes.Select(code => (JsonNode?)JsonValue.Create(code)).ToArray()),
            "One code per entry: a bare code (any system) or system|code, e.g. http://www.ama-assn.org/go/cpt|70553."),
        new("payerName", "Payer or plan name", CdsSettingTypes.Text, JsonValue.Create(string.Empty),
            "Optional; shown in the card."),
        new("submissionUrl", "Prior-authorization portal URL", CdsSettingTypes.Text, JsonValue.Create(string.Empty),
            "Optional absolute https URL added to the card as a link."),
        IndicatorSetting("indicator", "Card level", CdsIndicator.Warning, "Indicator for a prior-authorization card."),
    ];

    public override IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes { get; } =
    [
        CdsFixTypeCatalog.AddProblem(null),
        CdsFixTypeCatalog.AddEncounterDiagnosis(null),
    ];

    public override IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => NoPrefetch;

    public override CdsRequest BuildSampleRequest(string hook)
    {
        var mri = new JsonObject
        {
            ["resourceType"] = "ServiceRequest",
            ["id"] = "draft-mri-brain",
            ["status"] = "draft",
            ["intent"] = "order",
            ["code"] = Concept(CptSystem, "70553", "MRI brain without and with contrast"),
            ["subject"] = PatientReference(),
        };

        return SampleRequest(hook, [mri], prefetch: null);
    }

    protected override CdsHandlerResult Evaluate(CdsHandlerContext context, CancellationToken cancellationToken)
    {
        var codes = context.Settings.GetList("codes");
        if (codes.Count == 0)
        {
            return CdsHandlerResult.Empty;
        }

        var payer = context.Settings.GetText("payerName")?.Trim();
        var forPayer = string.IsNullOrEmpty(payer) ? string.Empty : $" by {payer}";
        var links = SubmissionLink(context.Settings.GetText("submissionUrl"));
        var indicator = context.Settings.GetText("indicator", CdsIndicator.Warning)!;

        var medications = Medications(context);
        var cards = new List<CdsCard>();
        foreach (var order in OrdersToCheck(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concept = CdsFhirReader.OrderConcept(order, medications);
            var matched = codes.Where(code => CdsFhirReader.HasCode(concept, [code])).ToArray();
            if (matched.Length == 0)
            {
                continue;
            }

            var label = CdsFhirReader.Label(concept, "the drafted order");
            cards.Add(context.CreateCard(
                $"Prior authorization likely required: {label}",
                indicator,
                Detail(
                    $"**{label}** is on the list of services that require prior authorization{forPayer} " +
                    $"(matched: {string.Join(", ", matched.Select(code => $"`{code}`"))}).",
                    "Start the prior-authorization request before scheduling, or choose an alternative."),
                CardLinks(context, links),
                context.OverrideReasons,
                PriorAuthTopic,
                DiagnosisFixes(context, null)));
        }

        return new CdsHandlerResult(cards);
    }

    private static IReadOnlyList<CdsLink>? SubmissionLink(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? [new CdsLink { Label = "Start prior authorization", Url = uri.ToString(), Type = "absolute" }]
            : null;
}
