using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// HL7 Da Vinci Coverage Requirements Discovery (CRD STU2): tells the clinician, while ordering, what the patient's
/// payer requires — covered or not, prior authorization, documentation — and annotates the order with the CRD
/// coverage-information extension through a systemAction (Epic: ServiceRequest.Update (Unsigned Order)).
/// <para>
/// Pure, like every handler: the coverage engine (<c>CdsCoverageEngine</c>, an <see cref="ICdsHandlerPreparer"/>)
/// has already resolved the Coverage, matched the payer and either applied the payer's rules or relayed the call to
/// the payer's own CRD service; its result arrives as <see cref="CdsHandlerContext.Prepared"/>. MedicationRequests
/// (and any type Epic cannot update) get an instructions card instead of a systemAction.
/// </para>
/// </summary>
public sealed class CoverageRequirementsCdsHandler : CdsServiceHandlerBase
{
    public const string UseSystemActionsKey = "useSystemActions";
    public const string IncludeInstructionsCardKey = "includeInstructionsCard";
    public const string NoRuleBehaviorKey = "noRuleBehavior";
    public const string DtrAppUrlKey = "dtrAppUrl";
    public const string DtrLinkTypeKey = "dtrLinkType";
    public const string TopicCodesKey = "topicCodes";
    public const string AuthNeededIndicatorKey = "authNeededIndicator";

    public const string NoRuleNone = "none";
    public const string NoRuleConditional = "conditional";
    public const string TopicsSegue = "segue";
    public const string TopicsCrd = "crd";

    /// <summary>The resource types a systemAction may update (Epic: ServiceRequest only, Feb 2024+). Never MedicationRequest.</summary>
    public static readonly IReadOnlySet<string> SystemActionResourceTypes =
        new HashSet<string>(StringComparer.Ordinal) { "ServiceRequest", "ProcedureRequest", "DeviceRequest", "NutritionOrder", "Appointment" };

    /// <summary>
    /// The patient's active Coverages with their payor Organizations (_include), so a payer can be matched by the
    /// Organization's identifier or name when Coverage.payor carries only a reference.
    /// </summary>
    private const string CoverageQuery = "Coverage?patient={{context.patientId}}&status=active&_include=Coverage:payor";
    private const string EncounterQuery = "Encounter?_id={{context.encounterId}}";

    public override string Key => CdsHandlerKeys.CoverageRequirements;

    public override string DisplayName => "Coverage requirements (CRD)";

    public override string Description =>
        "Da Vinci Coverage Requirements Discovery: tells the clinician whether the patient's payer covers the order, needs prior authorization or documentation, and adds the CRD coverage information to the order.";

    /// <summary>The payer rules are admin-entered (or relayed from the payer), not Segue's SAMPLE data.</summary>
    public override bool UsesSampleReferenceData => false;

    public override bool ReturnsSystemActions => true;

    public override IReadOnlyList<string> SupportedHooks { get; } =
    [
        CdsHookNames.OrderSign,
        CdsHookNames.OrderSelect,
        CdsHookNames.AppointmentBook,
        CdsHookNames.EncounterStart,
        CdsHookNames.EncounterDischarge,
    ];

    public override IReadOnlyList<CdsSettingDescriptor> Settings { get; } =
    [
        new(UseSystemActionsKey, "Add coverage information to orders (systemActions)", CdsSettingTypes.Boolean, JsonValue.Create(true),
            "Annotates ServiceRequest / DeviceRequest / Appointment with the CRD coverage-information extension. Epic needs the ServiceRequest.Update (Unsigned Order) API. MedicationRequests always get a card instead."),
        new(IncludeInstructionsCardKey, "Show an instructions card", CdsSettingTypes.Boolean, JsonValue.Create(true),
            "A plain-language card summarising the decision. Always shown when the decision cannot be added to the order."),
        new(NoRuleBehaviorKey, "When no rule matches", CdsSettingTypes.Select, JsonValue.Create(NoRuleNone),
            "What to show for an order the payer has no rule for (or a patient whose payer is not set up).",
            [new(NoRuleNone, "Nothing"), new(NoRuleConditional, "An information card: coverage requirements unknown")]),
        new(DtrAppUrlKey, "Documentation (DTR) app URL", CdsSettingTypes.Text, JsonValue.Create(string.Empty),
            "Optional https launch URL of the DTR SMART app. When set, orders that need documentation get a card with a link to it (it must be on the health system's SMART app allow-list)."),
        new(DtrLinkTypeKey, "Documentation link type", CdsSettingTypes.Select, JsonValue.Create(CdsCardLinkTypes.Smart),
            "smart: launched as a SMART app with appContext (order, coverage, questionnaire). absolute: a plain web link.",
            [new(CdsCardLinkTypes.Smart, "SMART app launch"), new(CdsCardLinkTypes.Absolute, "Web link")]),
        new(TopicCodesKey, "Card topic codes", CdsSettingTypes.Select, JsonValue.Create(TopicsSegue),
            "segue: letters-only codes (CoverageInformation, ...), which Epic requires. crd: the CRD card type code coverage-info from the CRD code system.",
            [new(TopicsSegue, "Segue (Epic compatible)"), new(TopicsCrd, "Da Vinci CRD card types")]),
        IndicatorSetting(AuthNeededIndicatorKey, "Card level when prior authorization is needed", CdsIndicator.Warning,
            "Indicator for a card saying prior authorization (or performer prior authorization) is needed."),
    ];

    public override IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) =>
        hook is CdsHookNames.EncounterStart or CdsHookNames.EncounterDischarge
            ? new Dictionary<string, string> { ["coverage"] = CoverageQuery, ["encounter"] = EncounterQuery }
            : new Dictionary<string, string> { ["coverage"] = CoverageQuery };

    public override CdsRequest BuildSampleRequest(string hook)
    {
        var coverage = SampleCoverageBundle();
        if (hook == CdsHookNames.AppointmentBook)
        {
            var request = SampleRequest(hook, [], new JsonObject { ["coverage"] = coverage });
            request.Context!["appointments"] = Bundle("collection", [SampleAppointment()]);
            return request;
        }

        if (hook is CdsHookNames.EncounterStart or CdsHookNames.EncounterDischarge)
        {
            return SampleRequest(hook, [], new JsonObject { ["coverage"] = coverage, ["encounter"] = SearchSet(SampleEncounter()) });
        }

        return SampleRequest(hook, [SampleImagingOrder("draft")], new JsonObject { ["coverage"] = coverage });
    }

    protected override CdsHandlerResult Evaluate(CdsHandlerContext context, CancellationToken cancellationToken)
    {
        if (context.Prepared is not CdsCoveragePrepared prepared)
        {
            return new CdsHandlerResult([], "Coverage data was not prepared for this call.");
        }

        var items = CdsCrdItems.Extract(context.Request);
        if (items.Count == 0)
        {
            return new CdsHandlerResult([], "The request has no order, appointment or encounter to check.");
        }

        var options = new Options(context.Settings);
        if (prepared.Source == CdsCoverageSource.RelayFailed)
        {
            return new CdsHandlerResult([], prepared.Note ?? "The payer's CRD service did not answer.");
        }

        if (prepared.Source == CdsCoverageSource.Relay)
        {
            return RelayAnswer(context, prepared, items, options, cancellationToken);
        }

        var cards = new List<CdsCard>();
        var actions = new List<CdsAction>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = prepared.Source == CdsCoverageSource.LocalRules ? prepared.Decisions.GetValueOrDefault(item.Key) : null;
            if (decision is null)
            {
                if (options.NoRule == NoRuleConditional)
                {
                    cards.Add(UnknownCard(context, item, prepared, options));
                }

                continue;
            }

            Emit(context, item, decision, prepared.PayerName ?? "the payer", prepared.CoverageReference, options, today, cards, actions);
        }

        return new CdsHandlerResult(cards, null, actions.Count > 0 ? actions : null);
    }

    /// <summary>
    /// The payer's own answer. Its cards are normalised (<see cref="NormalizeRelayedCard"/>: topic per the
    /// <c>topicCodes</c> setting, https links only, a SMART link only to the configured DTR app, no icon) and the
    /// evaluator then re-checks them and labels them "&lt;payer&gt; via Segue". Its systemActions are never forwarded
    /// as they are: only the well-formed coverage-information extensions are taken from them
    /// (<see cref="CdsCoverageInformation.Clean"/>), and the update is rebuilt on this request's own copy of the
    /// order, so a payer can add coverage information but never change anything else in the order. When this service
    /// may not send a systemAction for the item, the coverage information is shown as an instructions card instead.
    /// </summary>
    private CdsHandlerResult RelayAnswer(
        CdsHandlerContext context, CdsCoveragePrepared prepared, IReadOnlyList<CdsCrdItem> items, Options options, CancellationToken cancellationToken)
    {
        var response = prepared.RelayResponse ?? new CdsResponse();
        var payer = prepared.PayerName ?? "the payer";
        var cards = (response.Cards ?? []).Where(card => card is not null).Select(card => NormalizeRelayedCard(card, options)).ToList();
        var actions = new List<CdsAction>();
        var coverages = RequestCoverageReferences(context.Request, prepared.CoverageReference);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in response.SystemActions ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = action?.Resource;
            var reference = resource is null ? null : $"{CdsFhirReader.ResourceType(resource)}/{CdsFhirReader.Text(resource["id"])}";
            var item = items.FirstOrDefault(i => i.Reference is not null && i.Reference == reference);
            if (action is null || resource is null || item is null || !handled.Add(item.Key))
            {
                continue;
            }

            var extensions = (resource["extension"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(CdsCoverageInformation.IsCoverageInformation)
                .Select(extension => CdsCoverageInformation.Clean(extension, coverages))
                .OfType<JsonObject>()
                .ToArray();
            if (extensions.Length == 0)
            {
                continue;
            }

            var entries = CdsCoverageInformation.Read(new JsonObject { ["extension"] = new JsonArray(extensions.Select(e => (JsonNode)e.DeepClone()).ToArray()) });
            if (options.UseSystemActions && action.Type == CdsActionTypes.Update && SystemActionResourceTypes.Contains(item.ResourceType))
            {
                var annotated = item.Resource;
                foreach (var extension in extensions)
                {
                    annotated = CdsCoverageInformation.Annotate(annotated, extension, CdsCoverageInformation.CoverageOf(extension)!);
                }

                if (!JsonNode.DeepEquals(annotated, item.Resource))
                {
                    actions.Add(new CdsAction
                    {
                        Type = CdsActionTypes.Update,
                        Description = $"Coverage information from {payer}",
                        Resource = annotated,
                    });
                }

                if (options.IncludeInstructions)
                {
                    cards.AddRange(entries.Select(entry => InstructionsCard(context, item, entry.Decision, payer, annotated: true, options)));
                }

                continue;
            }

            cards.AddRange(entries.Select(entry => InstructionsCard(context, item, entry.Decision, payer, annotated: false, options)));
        }

        return new CdsHandlerResult(cards, null, actions.Count > 0 ? actions : null, $"{payer} via Segue");
    }

    /// <summary>The "Coverage/id" references a relayed coverage-information extension may name: this request's Coverages.</summary>
    private static IReadOnlySet<string> RequestCoverageReferences(CdsRequest request, string? matched)
    {
        var references = CdsFhirReader.Resources(request.Prefetch?["coverage"], "Coverage")
            .Select(coverage => CdsFhirReader.Text(coverage["id"]))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => $"Coverage/{id}")
            .ToHashSet(StringComparer.Ordinal);
        if (matched is not null)
        {
            references.Add(matched);
        }

        return references;
    }

    /// <summary>
    /// A relayed payer card made safe to show under Segue's name: its topic follows this service's
    /// <c>topicCodes</c> setting (Epic needs letters-only codes), its links are kept only when absolute https (a
    /// SMART link only when it launches this service's own DTR app URL; appContext at most
    /// <see cref="MaxRelayedAppContextLength"/> characters), and the payer's icon and card extension are dropped.
    /// </summary>
    private static CdsCard NormalizeRelayedCard(CdsCard card, Options options)
    {
        var topic = card.Source?.Topic;
        card.Source = new CdsSource { Label = card.Source?.Label ?? string.Empty, Topic = RelayedTopic(topic, options) };
        var dtr = Uri.TryCreate(options.DtrAppUrl, UriKind.Absolute, out var dtrUri) && dtrUri.Scheme == Uri.UriSchemeHttps ? dtrUri : null;
        var links = (card.Links ?? [])
            .Where(link => link is not null
                           && !string.IsNullOrWhiteSpace(link.Label)
                           && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
                           && uri.Scheme == Uri.UriSchemeHttps
                           && (link.Type == CdsCardLinkTypes.Absolute
                               || (link.Type == CdsCardLinkTypes.Smart && dtr is not null && SameApp(uri, dtr)))
                           && (link.AppContext is null || link.AppContext.Length <= MaxRelayedAppContextLength))
            .Select(link => new CdsLink
            {
                Label = link.Label.Trim().Length > 200 ? link.Label.Trim()[..200] : link.Label.Trim(),
                Url = link.Url,
                Type = link.Type,
                AppContext = link.Type == CdsCardLinkTypes.Smart ? link.AppContext : null,
            })
            .ToArray();
        card.Links = links.Length > 0 ? links : null;
        card.Extension = null;
        return card;
    }

    /// <summary>Longest appContext kept on a relayed SMART link.</summary>
    public const int MaxRelayedAppContextLength = 2048;

    private static bool SameApp(Uri link, Uri dtr) =>
        string.Equals(link.GetLeftPart(UriPartial.Path).TrimEnd('/'), dtr.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// segue: a letters-only code built from the payer's CRD card type ("coverage-info" → "CoverageInformation",
    /// "therapy-alternatives-opt" → "PayerTherapyAlternativesOpt"; none → "PayerCoverage"). crd: the payer's CRD code
    /// when it is a CRD card type code, else coverage-info.
    /// </summary>
    private static CdsCoding RelayedTopic(CdsCoding? topic, Options options)
    {
        var code = topic?.Code?.Trim();
        var isCrdCode = code is { Length: > 0 and <= 64 }
                        && code.All(c => c is (>= 'a' and <= 'z') or '-')
                        && (topic!.System is null || topic.System == CdsCoverageInformation.CrdTempSystem);
        if (options.Topics == TopicsCrd)
        {
            return isCrdCode
                ? new CdsCoding { System = CdsCoverageInformation.CrdTempSystem, Code = code!, Display = Clip(topic!.Display, 100) }
                : new CdsCoding { System = CdsCoverageInformation.CrdTempSystem, Code = "coverage-info", Display = "Coverage Information" };
        }

        if (!isCrdCode || code == "coverage-info")
        {
            return CdsTopic.Create(isCrdCode ? "CoverageInformation" : "PayerCoverage", isCrdCode ? "Coverage information" : "Payer coverage");
        }

        var letters = string.Concat(code!.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        var segueCode = "Payer" + letters;
        return CdsTopic.IsValidCode(segueCode)
            ? CdsTopic.Create(segueCode, Clip(topic!.Display, 100) ?? "Payer card")
            : CdsTopic.Create("PayerCoverage", "Payer coverage");
    }

    private static string? Clip(string? value, int max) =>
        value is null ? null : value.Length > max ? value[..max] : value;

    private void Emit(
        CdsHandlerContext context,
        CdsCrdItem item,
        CdsCoverageDecision decision,
        string payer,
        string? coverageReference,
        Options options,
        DateOnly today,
        List<CdsCard> cards,
        List<CdsAction> actions)
    {
        var annotated = false;
        if (options.UseSystemActions && item.Reference is not null && coverageReference is not null
            && SystemActionResourceTypes.Contains(item.ResourceType))
        {
            annotated = true;
            if (!CdsCoverageInformation.AlreadyCarries(item.Resource, decision, coverageReference))
            {
                var extension = CdsCoverageInformation.Build(decision, coverageReference, Guid.NewGuid().ToString(), today);
                actions.Add(new CdsAction
                {
                    Type = CdsActionTypes.Update,
                    Description = $"Coverage information from {payer}",
                    Resource = CdsCoverageInformation.Annotate(item.Resource, extension, coverageReference),
                });
            }
        }

        if (options.IncludeInstructions || !annotated)
        {
            cards.Add(InstructionsCard(context, item, decision, payer, annotated, options));
        }
        else if (PolicyLink(decision) is { } policy)
        {
            cards.Add(context.CreateCard(
                $"{payer} coverage policy: {item.Label}",
                CdsIndicator.Info,
                $"See {payer}'s policy for **{item.Label}**.",
                CardLinks(context, [policy]),
                context.OverrideReasons,
                Topic(options, "CoveragePolicy", "Payer coverage policy")));
        }

        if (decision.DocNeeded is not null && DtrLink(item, decision, coverageReference, payer, options) is { } dtr)
        {
            cards.Add(context.CreateCard(
                $"Documentation needed for {payer}: {item.Label}",
                CdsIndicator.Info,
                $"{payer} needs {DocText(decision.DocNeeded)} for **{item.Label}**. Open the documentation app to complete it.",
                CardLinks(context, [dtr]),
                context.OverrideReasons,
                Topic(options, "CoverageDocumentation", "Documentation needed")));
        }
    }

    private CdsCard InstructionsCard(
        CdsHandlerContext context, CdsCrdItem item, CdsCoverageDecision decision, string payer, bool annotated, Options options)
    {
        var lines = new List<string>
        {
            $"**{item.Label}** — {payer}:",
            $"- Coverage: {CoveredText(decision.Covered)}",
        };
        if (decision.PaNeeded is { } pa)
        {
            lines.Add($"- Prior authorization: {PaText(pa)}");
        }

        if (decision.DocNeeded is { } doc)
        {
            var purposes = decision.DocPurposes.Count > 0 ? $" ({string.Join(", ", decision.DocPurposes.Select(PurposeText))})" : string.Empty;
            lines.Add($"- Documentation: {DocText(doc)}{purposes}");
        }

        if (decision.InfoNeeded.Count > 0)
        {
            lines.Add($"- More information needed: {string.Join(", ", decision.InfoNeeded.Select(InfoText))}");
        }

        var paragraphs = new List<string> { string.Join("\n", lines) };
        if (!string.IsNullOrWhiteSpace(decision.Reason))
        {
            paragraphs.Add(decision.Reason!);
        }

        if (annotated)
        {
            paragraphs.Add("The coverage information was added to the order.");
        }

        var indicator = decision.Covered == CdsCoverageCodes.Covered.NotCovered
            ? CdsIndicator.Warning
            : decision.PaNeeded is CdsCoverageCodes.PaNeeded.AuthNeeded or CdsCoverageCodes.PaNeeded.PerformPa
                ? options.AuthNeededIndicator
                : CdsIndicator.Info;
        var own = PolicyLink(decision) is { } policy ? new[] { policy } : null;
        return context.CreateCard(
            Summary(decision, payer, item.Label),
            indicator,
            string.Join("\n\n", paragraphs),
            CardLinks(context, own),
            context.OverrideReasons,
            Topic(options, "CoverageInformation", "Coverage information"));
    }

    private CdsCard UnknownCard(CdsHandlerContext context, CdsCrdItem item, CdsCoveragePrepared prepared, Options options)
    {
        var why = prepared.Source switch
        {
            CdsCoverageSource.NoCoverage => "No active coverage was found for this patient.",
            CdsCoverageSource.NoPayer => "The patient's payer is not set up for coverage requirements.",
            _ => $"{prepared.PayerName ?? "The payer"} has no rule for this order.",
        };
        return context.CreateCard(
            $"Coverage requirements unknown: {item.Label}",
            CdsIndicator.Info,
            $"{why} Check coverage, prior authorization and documentation requirements with the payer for **{item.Label}**.",
            CardLinks(context),
            context.OverrideReasons,
            Topic(options, "CoverageUnknown", "Coverage requirements unknown"));
    }

    private static string Summary(CdsCoverageDecision decision, string payer, string label)
    {
        if (decision.Covered == CdsCoverageCodes.Covered.NotCovered)
        {
            return $"Not covered by {payer}: {label}";
        }

        return decision.PaNeeded switch
        {
            CdsCoverageCodes.PaNeeded.AuthNeeded => $"Prior authorization required by {payer}: {label}",
            CdsCoverageCodes.PaNeeded.PerformPa => $"Performer must get prior authorization from {payer}: {label}",
            CdsCoverageCodes.PaNeeded.Satisfied => $"Prior authorization already satisfied for {payer}: {label}",
            _ when decision.Covered == CdsCoverageCodes.Covered.Conditional || decision.PaNeeded == CdsCoverageCodes.PaNeeded.Conditional =>
                $"{payer} coverage depends on more information: {label}",
            _ when decision.DocNeeded is not null => $"Covered by {payer}, documentation needed: {label}",
            _ => $"Covered by {payer}, no prior authorization needed: {label}",
        };
    }

    private static CdsLink? PolicyLink(CdsCoverageDecision decision) =>
        Uri.TryCreate(decision.PolicyUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new CdsLink { Label = "Payer coverage policy", Url = uri.ToString(), Type = CdsCardLinkTypes.Absolute }
            : null;

    private static CdsLink? DtrLink(CdsCrdItem item, CdsCoverageDecision decision, string? coverageReference, string payer, Options options)
    {
        if (!Uri.TryCreate(options.DtrAppUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var smart = options.DtrLinkType == CdsCardLinkTypes.Smart;
        string? appContext = null;
        if (smart)
        {
            var app = new JsonObject();
            if (item.Reference is not null)
            {
                app["order"] = item.Reference;
            }

            if (coverageReference is not null)
            {
                app["coverage"] = coverageReference;
            }

            if (!string.IsNullOrWhiteSpace(decision.QuestionnaireCanonical))
            {
                app["questionnaire"] = new JsonArray(JsonValue.Create(decision.QuestionnaireCanonical));
            }

            appContext = app.ToJsonString();
        }

        return new CdsLink
        {
            Label = $"Complete {payer} documentation",
            Url = uri.ToString(),
            Type = smart ? CdsCardLinkTypes.Smart : CdsCardLinkTypes.Absolute,
            AppContext = appContext,
        };
    }

    private static CdsCoding Topic(Options options, string segueCode, string display) =>
        options.Topics == TopicsCrd
            ? new CdsCoding { System = CdsCoverageInformation.CrdTempSystem, Code = "coverage-info", Display = "Coverage Information" }
            : CdsTopic.Create(segueCode, display);

    private static string CoveredText(string covered) => covered switch
    {
        CdsCoverageCodes.Covered.IsCovered => "covered",
        CdsCoverageCodes.Covered.NotCovered => "not covered",
        _ => "depends on more information",
    };

    private static string PaText(string pa) => pa switch
    {
        CdsCoverageCodes.PaNeeded.NoAuth => "not needed",
        CdsCoverageCodes.PaNeeded.AuthNeeded => "needed",
        CdsCoverageCodes.PaNeeded.PerformPa => "needed, requested by the performing provider",
        CdsCoverageCodes.PaNeeded.Satisfied => "already satisfied",
        _ => "depends on more information",
    };

    private static string DocText(string doc) => doc switch
    {
        CdsCoverageCodes.DocNeeded.Clinical => "clinical documentation",
        CdsCoverageCodes.DocNeeded.Admin => "administrative documentation",
        CdsCoverageCodes.DocNeeded.Both => "clinical and administrative documentation",
        _ => "documentation, depending on more information",
    };

    private static string PurposeText(string purpose) => purpose switch
    {
        CdsCoverageCodes.DocPurpose.WithPa => "with the prior authorization",
        CdsCoverageCodes.DocPurpose.WithClaim => "with the claim",
        CdsCoverageCodes.DocPurpose.WithOrder => "with the order",
        CdsCoverageCodes.DocPurpose.RetainDoc => "kept on file",
        CdsCoverageCodes.DocPurpose.Other => "other",
        _ => purpose,
    };

    private static string InfoText(string info) => info switch
    {
        CdsCoverageCodes.InfoNeeded.Performer => "who will perform it",
        CdsCoverageCodes.InfoNeeded.Location => "where it will be done",
        CdsCoverageCodes.InfoNeeded.Timeframe => "when it will be done",
        CdsCoverageCodes.InfoNeeded.ContractWindow => "the contract window",
        CdsCoverageCodes.InfoNeeded.Other => "other information",
        _ => info,
    };

    // ---- Sample data (made up) ----

    /// <summary>The sample payer: matches a payer with identifier "EXAMPLE-PAYER" or name "Example Health Plan".</summary>
    private static JsonObject SampleCoverageBundle()
    {
        var coverage = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = "sample-coverage-1",
            ["status"] = "active",
            ["beneficiary"] = PatientReference(),
            ["payor"] = new JsonArray(new JsonObject
            {
                ["reference"] = "Organization/sample-payer-1",
                ["display"] = "Example Health Plan",
            }),
            ["class"] = new JsonArray(new JsonObject
            {
                ["type"] = Concept("http://terminology.hl7.org/CodeSystem/coverage-class", "plan", "Plan"),
                ["value"] = "EX-GOLD",
                ["name"] = "Example Gold",
            }),
        };
        var payer = new JsonObject
        {
            ["resourceType"] = "Organization",
            ["id"] = "sample-payer-1",
            ["name"] = "Example Health Plan",
            ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:oid:2.16.840.1.113883.3.9999", ["value"] = "EXAMPLE-PAYER" }),
        };
        return SearchSet(coverage, payer);
    }

    private static JsonObject SampleImagingOrder(string status) => new()
    {
        ["resourceType"] = "ServiceRequest",
        ["id"] = "draft-mri-brain",
        ["status"] = status,
        ["intent"] = "order",
        ["code"] = Concept(CdsSampleReferenceData.CptSystem, "70553", "MRI brain without and with contrast"),
        ["subject"] = PatientReference(),
        ["insurance"] = new JsonArray(new JsonObject { ["reference"] = "Coverage/sample-coverage-1" }),
    };

    private static JsonObject SampleAppointment() => new()
    {
        ["resourceType"] = "Appointment",
        ["id"] = "sample-appointment-1",
        ["status"] = "proposed",
        ["serviceType"] = new JsonArray(Concept(CdsSampleReferenceData.CptSystem, "97110", "Therapeutic exercise")),
        ["participant"] = new JsonArray(new JsonObject { ["actor"] = PatientReference(), ["status"] = "accepted" }),
    };

    private static JsonObject SampleEncounter() => new()
    {
        ["resourceType"] = "Encounter",
        ["id"] = SampleEncounterId,
        ["status"] = "in-progress",
        ["class"] = new JsonObject { ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ActCode", ["code"] = "IMP" },
        ["type"] = new JsonArray(Concept(CdsSampleReferenceData.CptSystem, "99221", "Initial hospital care")),
        ["subject"] = PatientReference(),
    };

    /// <summary>The instance's settings with defaults filled in, read once per call.</summary>
    private sealed class Options
    {
        public Options(CdsSettingsValues settings)
        {
            UseSystemActions = settings.GetBoolean(UseSystemActionsKey, true);
            IncludeInstructions = settings.GetBoolean(IncludeInstructionsCardKey, true);
            NoRule = settings.GetText(NoRuleBehaviorKey, NoRuleNone) ?? NoRuleNone;
            DtrAppUrl = settings.GetText(DtrAppUrlKey)?.Trim();
            DtrLinkType = settings.GetText(DtrLinkTypeKey, CdsCardLinkTypes.Smart) ?? CdsCardLinkTypes.Smart;
            Topics = settings.GetText(TopicCodesKey, TopicsSegue) ?? TopicsSegue;
            AuthNeededIndicator = settings.GetText(AuthNeededIndicatorKey, CdsIndicator.Warning) ?? CdsIndicator.Warning;
        }

        public bool UseSystemActions { get; }

        public bool IncludeInstructions { get; }

        public string NoRule { get; }

        public string? DtrAppUrl { get; }

        public string DtrLinkType { get; }

        public string Topics { get; }

        public string AuthNeededIndicator { get; }
    }
}
