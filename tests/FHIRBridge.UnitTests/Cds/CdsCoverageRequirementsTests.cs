using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Cds;
using FHIRBridge.Infrastructure.Cds.Coverage;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// Coverage requirements (Da Vinci CRD): rule and payer matching, the coverage-information extension, the pure
/// handler's systemActions and cards, the evaluator's systemAction gate, and the coverage engine's relay / fallback.
/// </summary>
public sealed class CdsCoverageRequirementsTests
{
    private const string Cpt = "http://www.ama-assn.org/go/cpt";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // ---- Domain rules ----

    private static CdsCoverageRuleValues Values(
        string code = "70553",
        bool prefix = false,
        string covered = "covered",
        string? pa = "auth-needed",
        string? doc = null,
        IReadOnlyList<string>? purposes = null,
        IReadOnlyList<string>? info = null,
        string? questionnaire = null,
        DateOnly? from = null,
        DateOnly? to = null,
        bool enabled = true,
        string system = Cpt) =>
        new(system, code, prefix, covered, pa, doc, purposes, info, "Reason text", "https://payer.example.org/policy", questionnaire, from, to, enabled);

    private static CdsCoverageRule Rule(Guid payerId, CdsCoverageRuleValues values) => new(payerId, values);

    [Fact]
    public void A_not_covered_rule_cannot_state_prior_authorization()
    {
        CdsCoverageRule.Validate(Values(covered: "not-covered", pa: "auth-needed"))
            .Should().Contain(e => e.Contains("not-covered", StringComparison.Ordinal));
    }

    [Fact]
    public void Info_needed_requires_a_conditional_value()
    {
        CdsCoverageRule.Validate(Values(info: ["performer"])).Should().NotBeEmpty();
        CdsCoverageRule.Validate(Values(covered: "conditional", pa: null, info: ["performer"])).Should().BeEmpty();
    }

    [Fact]
    public void A_rule_cannot_assert_satisfied_and_a_questionnaire_needs_documentation()
    {
        CdsCoverageRule.Validate(Values(pa: "satisfied")).Should().Contain(e => e.Contains("satisfied", StringComparison.Ordinal));
        CdsCoverageRule.Validate(Values(questionnaire: "http://payer.example.org/Questionnaire/q|1")).Should().NotBeEmpty();
        CdsCoverageRule.Validate(Values(doc: "clinical", questionnaire: "http://payer.example.org/Questionnaire/q|1")).Should().BeEmpty();
        var act = () => new CdsCoverageRule(Guid.NewGuid(), Values(covered: "maybe"));
        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void A_payer_needs_an_identifier_and_an_https_relay_url()
    {
        var noId = () => new CdsPayer("Acme", [], null, true);
        noId.Should().Throw<BusinessRuleException>();
        var http = () => new CdsPayer("Acme", ["ACME"], null, true, relayEnabled: true, relayServiceUrl: "http://crd.acme.example/cds-services/x");
        http.Should().Throw<BusinessRuleException>();
        var payer = new CdsPayer("Acme", ["ACME"], null, true);
        payer.ForwardFhirAuthorization.Should().BeFalse("the EHR's token is never forwarded unless an admin turns it on");
        payer.FallbackToLocalRules.Should().BeTrue();
    }

    // ---- Rule matching ----

    private static IReadOnlyList<FhirCode> Codes(string code, string system = Cpt) => [new FhirCode(system, code, null)];

    [Fact]
    public void An_exact_rule_beats_a_prefix_rule_and_a_longer_prefix_beats_a_shorter_one()
    {
        var payer = Guid.NewGuid();
        var shortPrefix = Rule(payer, Values(code: "70", prefix: true, pa: "no-auth"));
        var longPrefix = Rule(payer, Values(code: "705", prefix: true, pa: "conditional"));
        var exact = Rule(payer, Values(code: "70553"));

        CdsCoverageMatcher.MatchRule(Codes("70553"), [shortPrefix, longPrefix, exact], Today).Should().BeSameAs(exact);
        CdsCoverageMatcher.MatchRule(Codes("70551"), [shortPrefix, longPrefix, exact], Today).Should().BeSameAs(longPrefix);
        CdsCoverageMatcher.MatchRule(Codes("70010"), [shortPrefix, longPrefix, exact], Today).Should().BeSameAs(shortPrefix);
        CdsCoverageMatcher.MatchRule(Codes("99213"), [shortPrefix, longPrefix, exact], Today).Should().BeNull();
    }

    [Fact]
    public void A_prefix_is_used_only_when_the_rule_asks_for_it()
    {
        var rule = Rule(Guid.NewGuid(), Values(code: "705", prefix: false));
        CdsCoverageMatcher.MatchRule(Codes("70553"), [rule], Today).Should().BeNull();
    }

    [Fact]
    public void Effective_dates_and_the_enabled_switch_are_honoured()
    {
        var payer = Guid.NewGuid();
        var future = Rule(payer, Values(from: Today.AddDays(1)));
        var ended = Rule(payer, Values(from: Today.AddDays(-30), to: Today.AddDays(-1)));
        var off = Rule(payer, Values(enabled: false));
        CdsCoverageMatcher.MatchRule(Codes("70553"), [future, ended, off], Today).Should().BeNull();

        var current = Rule(payer, Values(from: Today.AddDays(-5), to: Today));
        var older = Rule(payer, Values(from: Today.AddDays(-50)));
        CdsCoverageMatcher.MatchRule(Codes("70553"), [older, current], Today).Should().BeSameAs(current, "the latest effective rule wins");
    }

    [Fact]
    public void The_cpt_oid_matches_the_cpt_url()
    {
        var rule = Rule(Guid.NewGuid(), Values());
        CdsCoverageMatcher.MatchRule(Codes("70553", "urn:oid:2.16.840.1.113883.6.12"), [rule], Today).Should().BeSameAs(rule);
        CdsCoverageMatcher.MatchRule(Codes("70553", "http://loinc.org"), [rule], Today).Should().BeNull();
    }

    // ---- Payer matching ----

    private static JsonObject CoverageBundle(string? display = "Acme Health", string? identifier = null, string? plan = null, string id = "cov-1", int? order = null)
    {
        var payor = new JsonObject();
        if (display is not null)
        {
            payor["display"] = display;
        }

        if (identifier is not null)
        {
            payor["identifier"] = new JsonObject { ["system"] = "urn:payer-ids", ["value"] = identifier };
        }

        var coverage = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = id,
            ["status"] = "active",
            ["payor"] = new JsonArray(payor),
        };
        if (order is not null)
        {
            coverage["order"] = order;
        }

        if (plan is not null)
        {
            coverage["class"] = new JsonArray(new JsonObject { ["value"] = plan });
        }

        return CdsTestSupport.SearchSet(coverage);
    }

    private static CdsRequest CoverageRequest(JsonObject bundle) =>
        new() { Hook = CdsHookNames.OrderSign, HookInstance = Guid.NewGuid().ToString(), Context = new JsonObject(), Prefetch = new JsonObject { ["coverage"] = bundle } };

    [Theory]
    [InlineData("Acme Health")]
    [InlineData("acme health")]
    [InlineData("ACME-1")]
    [InlineData("urn:payer-ids|ACME-1")]
    public void A_payer_matches_by_display_identifier_or_system_and_value(string payerIdentifier)
    {
        var bundle = CoverageBundle(identifier: "ACME-1");
        var payer = new CdsPayer("Acme", [payerIdentifier], null, true);

        var match = CdsCoverageMatcher.MatchPayer(CdsCoverageMatcher.ActiveCoverages(CoverageRequest(bundle)), bundle, [payer]);

        match!.Payer.Should().BeSameAs(payer);
        match.CoverageReference.Should().Be("Coverage/cov-1");
    }

    [Fact]
    public void Plan_values_must_match_when_set_and_the_wrong_system_does_not_match()
    {
        var bundle = CoverageBundle(identifier: "ACME-1", plan: "GOLD");
        var coverages = CdsCoverageMatcher.ActiveCoverages(CoverageRequest(bundle));

        CdsCoverageMatcher.MatchPayer(coverages, bundle, [new CdsPayer("Acme", ["ACME-1"], ["SILVER"], true)]).Should().BeNull();
        CdsCoverageMatcher.MatchPayer(coverages, bundle, [new CdsPayer("Acme", ["ACME-1"], ["gold"], true)]).Should().NotBeNull();
        CdsCoverageMatcher.MatchPayer(coverages, bundle, [new CdsPayer("Acme", ["urn:other|ACME-1"], null, true)]).Should().BeNull();
        CdsCoverageMatcher.MatchPayer(coverages, bundle, [new CdsPayer("Acme", ["ACME-1"], null, false)]).Should().BeNull("a disabled payer never matches");
    }

    [Fact]
    public void A_payer_matches_through_the_referenced_organization_and_the_primary_coverage_wins()
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["entry"] = new JsonArray(
                new JsonObject { ["resource"] = new JsonObject { ["resourceType"] = "Coverage", ["id"] = "secondary", ["status"] = "active", ["order"] = 2, ["payor"] = new JsonArray(new JsonObject { ["display"] = "Beta" }) } },
                new JsonObject { ["resource"] = new JsonObject { ["resourceType"] = "Coverage", ["id"] = "primary", ["status"] = "active", ["order"] = 1, ["payor"] = new JsonArray(new JsonObject { ["reference"] = "Organization/org-1" }) } },
                new JsonObject { ["resource"] = new JsonObject { ["resourceType"] = "Coverage", ["id"] = "old", ["status"] = "cancelled", ["payor"] = new JsonArray(new JsonObject { ["display"] = "Gamma" }) } },
                new JsonObject { ["resource"] = new JsonObject { ["resourceType"] = "Organization", ["id"] = "org-1", ["name"] = "Alpha Plan" } }),
        };
        var coverages = CdsCoverageMatcher.ActiveCoverages(CoverageRequest(bundle));
        coverages.Select(c => c["id"]!.GetValue<string>()).Should().Equal("primary", "secondary");

        var alpha = new CdsPayer("Alpha", ["Alpha Plan"], null, true);
        var beta = new CdsPayer("Beta", ["Beta"], null, true);
        var gamma = new CdsPayer("Gamma", ["Gamma"], null, true);
        CdsCoverageMatcher.MatchPayer(coverages, bundle, [beta, alpha])!.CoverageReference.Should().Be("Coverage/primary");
        CdsCoverageMatcher.MatchPayer(coverages, bundle, [gamma]).Should().BeNull("a cancelled coverage is ignored");
    }

    // ---- The coverage-information extension ----

    [Fact]
    public void The_extension_has_the_crd_shape()
    {
        var decision = new CdsCoverageDecision("covered", "auth-needed", "clinical", ["withpa"], [], "Needs PA.", null,
            "http://payer.example.org/Questionnaire/mri|1.0");
        var assertion = Guid.NewGuid().ToString();

        var extension = CdsCoverageInformation.Build(decision, "Coverage/cov-1", assertion, new DateOnly(2026, 10, 16));

        extension["url"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information");
        var parts = extension["extension"]!.AsArray().Select(p => p!.AsObject()).ToArray();
        parts.Select(p => p["url"]!.GetValue<string>()).Should().Equal(
            "coverage", "covered", "pa-needed", "doc-needed", "doc-purpose", "reason", "questionnaire", "date", "coverage-assertion-id");
        parts[0]["valueReference"]!["reference"]!.GetValue<string>().Should().Be("Coverage/cov-1");
        parts[1]["valueCode"]!.GetValue<string>().Should().Be("covered");
        parts[2]["valueCode"]!.GetValue<string>().Should().Be("auth-needed");
        parts[3]["valueCode"]!.GetValue<string>().Should().Be("clinical");
        parts[4]["valueCode"]!.GetValue<string>().Should().Be("withpa");
        parts[5]["valueCodeableConcept"]!["text"]!.GetValue<string>().Should().Be("Needs PA.");
        parts[6]["valueCanonical"]!.GetValue<string>().Should().Be("http://payer.example.org/Questionnaire/mri|1.0");
        parts[7]["valueDate"]!.GetValue<string>().Should().Be("2026-10-16");
        parts[8]["valueString"]!.GetValue<string>().Should().Be(assertion);

        var read = CdsCoverageInformation.Read(new JsonObject { ["extension"] = new JsonArray(extension) }).Should().ContainSingle().Subject;
        read.CoverageReference.Should().Be("Coverage/cov-1");
        read.AssertionId.Should().Be(assertion);
        read.Decision.Codes.Should().Be("covered/auth-needed/clinical");
    }

    [Fact]
    public void Annotating_replaces_the_extension_for_the_same_coverage_and_keeps_others()
    {
        var decision = new CdsCoverageDecision("covered", "no-auth", null, [], [], null, null, null);
        var order = new JsonObject
        {
            ["resourceType"] = "ServiceRequest",
            ["id"] = "sr-1",
            ["extension"] = new JsonArray(
                new JsonObject { ["url"] = "http://example.org/other", ["valueString"] = "keep" },
                CdsCoverageInformation.Build(decision with { PaNeeded = "auth-needed" }, "Coverage/cov-1", "old", Today)),
        };

        var annotated = CdsCoverageInformation.Annotate(order, CdsCoverageInformation.Build(decision, "Coverage/cov-1", "new", Today), "Coverage/cov-1");

        annotated["extension"]!.AsArray().Should().HaveCount(2);
        CdsCoverageInformation.Read(annotated).Should().ContainSingle().Which.AssertionId.Should().Be("new");
        order["extension"]!.AsArray().Should().HaveCount(2, "the request's own order is not changed");
    }

    // ---- The handler ----

    private static readonly CoverageRequirementsCdsHandler Handler = new();

    private static JsonObject ServiceRequest(string id = "sr-mri", string code = "70553") => new()
    {
        ["resourceType"] = "ServiceRequest",
        ["id"] = id,
        ["status"] = "draft",
        ["intent"] = "order",
        ["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["system"] = Cpt, ["code"] = code, ["display"] = "MRI brain" }) },
    };

    private static JsonObject MedicationRequest(string id = "mr-1") => new()
    {
        ["resourceType"] = "MedicationRequest",
        ["id"] = id,
        ["status"] = "draft",
        ["intent"] = "order",
        ["medicationCodeableConcept"] = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject { ["system"] = "http://www.nlm.nih.gov/research/umls/rxnorm", ["code"] = "123", ["display"] = "Drug X" }),
        },
    };

    private static CdsRequest OrderSign(params JsonObject[] orders) => new()
    {
        Hook = CdsHookNames.OrderSign,
        HookInstance = Guid.NewGuid().ToString(),
        Context = new JsonObject
        {
            ["patientId"] = "p-1",
            ["userId"] = "Practitioner/u-1",
            ["draftOrders"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(orders.Select(o => (JsonNode?)new JsonObject { ["resource"] = o }).ToArray()),
            },
        },
        Prefetch = new JsonObject { ["coverage"] = CoverageBundle() },
    };

    private static CdsCoveragePrepared Local(IReadOnlyDictionary<string, CdsCoverageDecision?> decisions) =>
        new(CdsCoverageSource.LocalRules, Guid.NewGuid(), "Acme", "Coverage/cov-1", decisions);

    private static readonly CdsCoverageDecision AuthNeeded =
        new("covered", "auth-needed", "clinical", ["withpa"], [], "MRI needs prior authorization.", "https://payer.example.org/policies/mri", null);

    private static async Task<CdsHandlerResult> RunAsync(CdsRequest request, object prepared, string? settings = null)
    {
        var context = new CdsHandlerContext(
            new CdsInstanceSnapshot(Guid.NewGuid(), "crd", "CRD", Handler.Key, request.Hook, "Segue CRD", null),
            request,
            CdsSettingsValues.Resolve(Handler.Settings, settings),
            prepared);
        return await Handler.EvaluateAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task A_service_request_gets_a_system_action_update_with_coverage_information_and_an_instructions_card()
    {
        var request = OrderSign(ServiceRequest());

        var result = await RunAsync(request, Local(new Dictionary<string, CdsCoverageDecision?> { ["ServiceRequest/sr-mri"] = AuthNeeded }));

        var action = result.SystemActions.Should().ContainSingle().Subject;
        action.Type.Should().Be("update");
        action.Resource!["id"]!.GetValue<string>().Should().Be("sr-mri");
        var info = CdsCoverageInformation.Read(action.Resource).Should().ContainSingle().Subject;
        info.Decision.Codes.Should().Be("covered/auth-needed/clinical");
        Guid.TryParse(info.AssertionId, out _).Should().BeTrue("coverage-assertion-id is a generated uuid");
        var card = result.Cards.Should().ContainSingle().Subject;
        card.Summary.Should().Contain("Prior authorization required by Acme");
        card.Indicator.Should().Be("warning");
        card.Source.Topic!.Code.Should().Be("CoverageInformation");
        card.Links!.Single().Url.Should().Be("https://payer.example.org/policies/mri");
        result.DegradedReason.Should().BeNull();
    }

    [Fact]
    public async Task A_medication_request_gets_a_card_and_never_a_system_action()
    {
        var request = OrderSign(MedicationRequest());

        var result = await RunAsync(request, Local(new Dictionary<string, CdsCoverageDecision?> { ["MedicationRequest/mr-1"] = AuthNeeded }));

        result.SystemActions.Should().BeNull();
        result.Cards.Should().ContainSingle().Which.Detail.Should().NotContain("added to the order");
    }

    [Fact]
    public async Task With_system_actions_off_the_decision_is_a_card_only_and_with_instructions_off_only_the_action_and_policy_card_remain()
    {
        var request = OrderSign(ServiceRequest());
        var prepared = Local(new Dictionary<string, CdsCoverageDecision?> { ["ServiceRequest/sr-mri"] = AuthNeeded });

        var cardsOnly = await RunAsync(request, prepared, """{"useSystemActions":false}""");
        cardsOnly.SystemActions.Should().BeNull();
        cardsOnly.Cards.Should().ContainSingle();

        var actionOnly = await RunAsync(request, prepared, """{"includeInstructionsCard":false}""");
        actionOnly.SystemActions.Should().ContainSingle();
        actionOnly.Cards.Should().ContainSingle().Which.Source.Topic!.Code.Should().Be("CoveragePolicy");
    }

    [Fact]
    public async Task No_rule_gives_nothing_by_default_and_an_unknown_card_when_asked()
    {
        var request = OrderSign(ServiceRequest());
        var prepared = Local(new Dictionary<string, CdsCoverageDecision?> { ["ServiceRequest/sr-mri"] = null });

        (await RunAsync(request, prepared)).Cards.Should().BeEmpty();
        var conditional = await RunAsync(request, prepared, """{"noRuleBehavior":"conditional"}""");
        conditional.Cards.Should().ContainSingle().Which.Source.Topic!.Code.Should().Be("CoverageUnknown");
        conditional.SystemActions.Should().BeNull();
    }

    [Fact]
    public async Task An_order_already_carrying_the_same_decision_is_not_updated_again()
    {
        var order = ServiceRequest();
        order["extension"] = new JsonArray(CdsCoverageInformation.Build(AuthNeeded, "Coverage/cov-1", "earlier", Today));

        var result = await RunAsync(OrderSign(order), Local(new Dictionary<string, CdsCoverageDecision?> { ["ServiceRequest/sr-mri"] = AuthNeeded }));

        result.SystemActions.Should().BeNull();
        result.Cards.Should().ContainSingle();
    }

    [Fact]
    public async Task Documentation_needed_with_a_dtr_app_gets_a_smart_launch_card_and_crd_topics_use_the_crd_code_system()
    {
        var decision = AuthNeeded with { QuestionnaireCanonical = "http://payer.example.org/Questionnaire/mri|1" };
        var result = await RunAsync(
            OrderSign(ServiceRequest()),
            Local(new Dictionary<string, CdsCoverageDecision?> { ["ServiceRequest/sr-mri"] = decision }),
            """{"dtrAppUrl":"https://dtr.example.org/launch","topicCodes":"crd"}""");

        var dtr = result.Cards.Single(card => card.Links?.Any(link => link.Type == "smart") == true);
        var link = dtr.Links!.Single(l => l.Type == "smart");
        var appContext = JsonNode.Parse(link.AppContext!)!;
        appContext["order"]!.GetValue<string>().Should().Be("ServiceRequest/sr-mri");
        appContext["coverage"]!.GetValue<string>().Should().Be("Coverage/cov-1");
        appContext["questionnaire"]![0]!.GetValue<string>().Should().Be("http://payer.example.org/Questionnaire/mri|1");
        result.Cards.Should().OnlyContain(card => card.Source.Topic!.System == "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp"
                                                  && card.Source.Topic.Code == "coverage-info");
    }

    [Fact]
    public async Task An_appointment_is_annotated_too()
    {
        var request = Handler.BuildSampleRequest(CdsHookNames.AppointmentBook);
        var decision = new CdsCoverageDecision("covered", "no-auth", null, [], [], null, null, null);

        var result = await RunAsync(request, Local(new Dictionary<string, CdsCoverageDecision?> { ["Appointment/sample-appointment-1"] = decision }));

        result.SystemActions.Should().ContainSingle().Which.Resource!["resourceType"]!.GetValue<string>().Should().Be("Appointment");
    }

    [Fact]
    public async Task A_relayed_answer_keeps_valid_payer_actions_converts_the_rest_to_cards_and_is_labelled_via_segue()
    {
        var request = OrderSign(ServiceRequest(), MedicationRequest());
        var extension = CdsCoverageInformation.Build(AuthNeeded, "Coverage/cov-1", "payer-id", Today);
        var relayResponse = new CdsResponse
        {
            Cards = [new CdsCard { Summary = "Payer card", Indicator = "info" }],
            SystemActions =
            [
                new CdsAction { Type = "update", Description = "x", Resource = CdsCoverageInformation.Annotate(ServiceRequest(), extension, "Coverage/cov-1") },
                new CdsAction { Type = "update", Description = "x", Resource = CdsCoverageInformation.Annotate(MedicationRequest(), extension, "Coverage/cov-1") },
            ],
        };
        var prepared = new CdsCoveragePrepared(CdsCoverageSource.Relay, Guid.NewGuid(), "Acme", "Coverage/cov-1",
            new Dictionary<string, CdsCoverageDecision?>(), relayResponse);

        var result = await RunAsync(request, prepared, """{"includeInstructionsCard":false}""");

        result.SourceLabel.Should().Be("Acme via Segue");
        result.SystemActions.Should().ContainSingle().Which.Resource!["resourceType"]!.GetValue<string>().Should().Be("ServiceRequest");
        result.Cards.Should().HaveCount(2, "the payer's card plus a card for the MedicationRequest's coverage information");
    }

    [Fact]
    public async Task A_relayed_system_action_can_only_add_coverage_information_never_change_the_order()
    {
        var request = OrderSign(ServiceRequest());
        var extension = CdsCoverageInformation.Build(AuthNeeded, "Coverage/cov-1", "payer-id", Today);
        ((JsonArray)extension["extension"]!).Add(new JsonObject { ["url"] = "evil", ["valueString"] = "x" });
        var rewritten = CdsCoverageInformation.Annotate(ServiceRequest(code: "99999"), extension, "Coverage/cov-1");
        rewritten["status"] = "revoked";
        rewritten["quantityQuantity"] = new JsonObject { ["value"] = 10 };
        var foreignCoverage = CdsCoverageInformation.Build(AuthNeeded, "Coverage/someone-else", "payer-id-2", Today);
        ((JsonArray)rewritten["extension"]!).Add(foreignCoverage);
        var relayResponse = new CdsResponse
        {
            SystemActions = [new CdsAction { Type = "update", Description = "x", Resource = rewritten }],
        };
        var prepared = new CdsCoveragePrepared(CdsCoverageSource.Relay, Guid.NewGuid(), "Acme", "Coverage/cov-1",
            new Dictionary<string, CdsCoverageDecision?>(), relayResponse);

        var result = await RunAsync(request, prepared);

        var resource = result.SystemActions.Should().ContainSingle().Which.Resource!;
        resource["status"]!.GetValue<string>().Should().Be("draft", "the order is rebuilt from the request's own copy");
        resource["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("70553");
        resource["quantityQuantity"].Should().BeNull();
        var extensions = resource["extension"]!.AsArray();
        extensions.Should().ContainSingle("only the coverage information for this request's Coverage is kept");
        var parts = extensions[0]!["extension"]!.AsArray().Select(p => p!["url"]!.GetValue<string>()).ToArray();
        parts.Should().NotContain("evil");
        parts.Should().Contain(["coverage", "covered", "pa-needed", "date", "coverage-assertion-id"]);
    }

    [Fact]
    public async Task Relayed_payer_cards_get_safe_links_no_icon_and_an_epic_safe_topic()
    {
        var request = OrderSign(ServiceRequest());
        CdsResponse RelayResponse() => new()
        {
            Cards =
            [
                new CdsCard
                {
                    Summary = "Payer card",
                    Indicator = "info",
                    Source = new CdsSource
                    {
                        Label = "Payer",
                        Icon = "https://tracker.example/pixel.png",
                        Topic = new CdsCoding { System = CdsCoverageInformation.CrdTempSystem, Code = "coverage-info" },
                    },
                    Links =
                    [
                        new CdsLink { Label = "Evil app", Url = "https://evil.example/launch", Type = "smart", AppContext = "{}" },
                        new CdsLink { Label = "Plain", Url = "http://payer.example/policy", Type = "absolute" },
                        new CdsLink { Label = "Script", Url = "javascript:alert(1)", Type = "absolute" },
                        new CdsLink { Label = "Policy", Url = "https://payer.example/policy", Type = "absolute" },
                        new CdsLink { Label = "DTR", Url = "https://dtr.example.org/launch", Type = "smart", AppContext = "{\"order\":\"x\"}" },
                    ],
                },
                new CdsCard
                {
                    Summary = "Alternatives",
                    Indicator = "info",
                    Source = new CdsSource { Label = "Payer", Topic = new CdsCoding { Code = "therapy-alternatives-opt" } },
                },
            ],
        };
        CdsCoveragePrepared Prepared() => new(CdsCoverageSource.Relay, Guid.NewGuid(), "Acme", "Coverage/cov-1",
            new Dictionary<string, CdsCoverageDecision?>(), RelayResponse());

        var result = await RunAsync(request, Prepared(), """{"dtrAppUrl":"https://dtr.example.org/launch"}""");

        var card = result.Cards[0];
        card.Links!.Select(l => l.Label).Should().BeEquivalentTo(["Policy", "DTR"], "only https links, and a SMART link only to the configured DTR app");
        card.Source.Icon.Should().BeNull();
        card.Source.Topic!.Code.Should().Be("CoverageInformation");
        CdsTopic.IsValidCode(result.Cards[1].Source.Topic!.Code).Should().BeTrue();
        result.Cards[1].Source.Topic!.Code.Should().Be("PayerTherapyAlternativesOpt");

        var crd = await RunAsync(request, Prepared(), """{"topicCodes":"crd"}""");
        crd.Cards[1].Source.Topic!.Code.Should().Be("therapy-alternatives-opt");
        crd.Cards[0].Links!.Select(l => l.Label).Should().BeEquivalentTo(["Policy"], "with no DTR app configured no SMART link is kept");
    }

    [Fact]
    public async Task A_failed_relay_without_fallback_answers_with_nothing_and_a_degraded_reason()
    {
        var prepared = new CdsCoveragePrepared(CdsCoverageSource.RelayFailed, Guid.NewGuid(), "Acme", null,
            new Dictionary<string, CdsCoverageDecision?>(), null, "The payer's CRD service did not answer (timed out); answered with no cards.");

        var result = await RunAsync(OrderSign(ServiceRequest()), prepared);

        result.Cards.Should().BeEmpty();
        result.DegradedReason.Should().Contain("timed out");
    }

    [Fact]
    public void The_handler_supports_the_crd_hooks_and_returns_system_actions()
    {
        Handler.SupportedHooks.Should().BeEquivalentTo(
            ["order-sign", "order-select", "appointment-book", "encounter-start", "encounter-discharge"],
            "order-dispatch is not offered: its dispatched orders are references a prefetch template cannot fetch");
        ((ICdsServiceHandler)Handler).ReturnsSystemActions.Should().BeTrue();
        ((ICdsServiceHandler)new PriorAuthCdsHandler()).ReturnsSystemActions.Should().BeFalse();
        Handler.GetPrefetchTemplates(CdsHookNames.OrderSign)["coverage"].Should().Be("Coverage?patient={{context.patientId}}&status=active&_include=Coverage:payor");
        Handler.GetPrefetchTemplates(CdsHookNames.EncounterStart).Keys.Should().BeEquivalentTo(["coverage", "encounter"]);
        Handler.UsesSampleReferenceData.Should().BeFalse();
    }

    // ---- The evaluator's systemAction gate ----

    private sealed class ActionHandler : ICdsServiceHandler
    {
        private readonly bool _returnsActions;
        private readonly Func<CdsRequest, IReadOnlyList<CdsAction>> _actions;

        public ActionHandler(bool returnsActions, Func<CdsRequest, IReadOnlyList<CdsAction>> actions)
        {
            _returnsActions = returnsActions;
            _actions = actions;
        }

        public string Key => "action-test";
        public string DisplayName => "Action test";
        public string Description => "Test.";
        public IReadOnlyList<string> SupportedHooks => [CdsHookNames.OrderSign];
        public IReadOnlyList<CdsSettingDescriptor> Settings => [];
        public IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes => [];
        public bool ReturnsSystemActions => _returnsActions;
        public IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> GetFallbackQueries(string hook) => new Dictionary<string, string>();
        public CdsRequest BuildSampleRequest(string hook) => throw new NotSupportedException();

        public Task<CdsHandlerResult> EvaluateAsync(CdsHandlerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CdsHandlerResult([], null, _actions(context.Request)));
    }

    private static CdsServiceInstance Instance(string handlerKey) =>
        new("action-test", "Action test", handlerKey, CdsHookNames.OrderSign, "Test.", true, null, "Segue", null, null);

    private static CdsAction Update(JsonObject resource) => new()
    {
        Type = "update",
        Description = "Coverage",
        Resource = CdsCoverageInformation.Annotate(resource, CdsCoverageInformation.Build(AuthNeeded, "Coverage/cov-1", "a", Today), "Coverage/cov-1"),
    };

    [Fact]
    public async Task System_actions_from_a_handler_that_may_not_send_them_are_dropped()
    {
        var handler = new ActionHandler(false, _ => [Update(ServiceRequest())]);

        var evaluation = await CdsTestSupport.Evaluator().EvaluateAsync(Instance(handler.Key), handler, OrderSign(ServiceRequest()), CancellationToken.None);

        evaluation.Response!.SystemActions.Should().BeNull();
        evaluation.Outcome.Should().Be(CdsCallOutcome.NoCards);
    }

    [Fact]
    public async Task Only_updates_of_the_requests_own_resources_with_coverage_information_leave_segue()
    {
        var handler = new ActionHandler(true, _ =>
        [
            Update(ServiceRequest()),
            Update(ServiceRequest("not-in-request")),
            new CdsAction { Type = "create", Description = "x", Resource = ServiceRequest() },
            new CdsAction { Type = "update", Description = "x", Resource = ServiceRequest() },
        ]);

        var evaluation = await CdsTestSupport.Evaluator().EvaluateAsync(Instance(handler.Key), handler, OrderSign(ServiceRequest()), CancellationToken.None);

        evaluation.Response!.SystemActions.Should().ContainSingle().Which.Resource!["id"]!.GetValue<string>().Should().Be("sr-mri");
        evaluation.Outcome.Should().Be(CdsCallOutcome.Success, "a systemAction counts as an answer");
        evaluation.CoverageAssertionCount.Should().Be(1);
    }

    [Fact]
    public async Task An_update_that_changes_anything_but_coverage_information_is_dropped_by_the_evaluator()
    {
        var changed = Update(ServiceRequest());
        changed.Resource!["status"] = "revoked";
        var handler = new ActionHandler(true, _ => [changed]);

        var evaluation = await CdsTestSupport.Evaluator().EvaluateAsync(Instance(handler.Key), handler, OrderSign(ServiceRequest()), CancellationToken.None);

        evaluation.Response!.SystemActions.Should().BeNull();
    }

    [Fact]
    public async Task The_evaluator_drops_card_links_and_icons_that_are_not_https()
    {
        var handler = new CardHandler(new CdsCard
        {
            Summary = "A card",
            Indicator = "info",
            Source = new CdsSource { Label = "x", Icon = "http://icon.example/i.png" },
            Links =
            [
                new CdsLink { Label = "Bad", Url = "data:text/html,hi", Type = "absolute" },
                new CdsLink { Label = "Odd type", Url = "https://ok.example", Type = "weird" },
                new CdsLink { Label = "Good", Url = "https://ok.example", Type = "absolute" },
            ],
        });

        var evaluation = await CdsTestSupport.Evaluator().EvaluateAsync(Instance(handler.Key), handler, OrderSign(ServiceRequest()), CancellationToken.None);

        var card = evaluation.Response!.Cards.Should().ContainSingle().Subject;
        card.Links!.Select(l => l.Label).Should().BeEquivalentTo(["Good"]);
        card.Source.Icon.Should().BeNull();
    }

    private sealed class CardHandler : ICdsServiceHandler
    {
        private readonly CdsCard _card;

        public CardHandler(CdsCard card)
        {
            _card = card;
        }

        public string Key => "card-test";
        public string DisplayName => "Card test";
        public string Description => "Test.";
        public IReadOnlyList<string> SupportedHooks => [CdsHookNames.OrderSign];
        public IReadOnlyList<CdsSettingDescriptor> Settings => [];
        public IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes => [];
        public bool ReturnsSystemActions => false;
        public IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> GetFallbackQueries(string hook) => new Dictionary<string, string>();
        public CdsRequest BuildSampleRequest(string hook) => throw new NotSupportedException();

        public Task<CdsHandlerResult> EvaluateAsync(CdsHandlerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CdsHandlerResult([_card], null));
    }

    // ---- The coverage engine ----

    private sealed class FakeCache : ICdsCoverageRuleCache
    {
        private readonly CdsCoverageRuleSnapshot _snapshot;

        public FakeCache(CdsCoverageRuleSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<CdsCoverageRuleSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

        public void Invalidate()
        {
        }
    }

    private sealed class FakeRelay : ICdsPayerRelayClient
    {
        private readonly Func<CdsRequest, CdsRelayResult> _answer;

        public FakeRelay(Func<CdsRequest, CdsRelayResult> answer)
        {
            _answer = answer;
        }

        public List<CdsRequest> Sent { get; } = [];

        public Task<CdsRelayResult> SendAsync(CdsPayer payer, CdsRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(_answer(request));
        }
    }

    private static (CdsCoverageEngine Engine, FakeRelay Relay) Engine(CdsPayer payer, IEnumerable<CdsCoverageRule> rules, Func<CdsRequest, CdsRelayResult>? relay = null)
    {
        var fake = new FakeRelay(relay ?? (_ => new CdsRelayResult(true, "ok", 200, 12, new CdsResponse())));
        return (new CdsCoverageEngine(new FakeCache(new CdsCoverageRuleSnapshot([payer], rules.ToArray())), fake, NullLogger<CdsCoverageEngine>.Instance), fake);
    }

    private static CdsPreparationContext Prep(CdsRequest request, bool live = true) =>
        new(new CdsInstanceSnapshot(Guid.NewGuid(), "crd", "CRD", Handler.Key, request.Hook, "Segue", null), request,
            CdsSettingsValues.Resolve(Handler.Settings, null), TimeSpan.FromMilliseconds(1500), live);

    private static CdsRequest WithAuthorization(CdsRequest request)
    {
        request.FhirServer = "https://ehr.example.org/api/FHIR/R4";
        request.FhirAuthorization = new CdsFhirAuthorization { AccessToken = "ehr-secret-token", Subject = "client" };
        return request;
    }

    [Fact]
    public async Task Local_rules_give_decisions_per_order_and_call_log_codes()
    {
        var payer = new CdsPayer("Acme", ["Acme Health"], null, true);
        var (engine, relay) = Engine(payer, [Rule(payer.Id, Values())]);

        var preparation = await engine.PrepareAsync(Prep(OrderSign(ServiceRequest(), ServiceRequest("sr-2", "99999"))), CancellationToken.None);

        var prepared = preparation.Data.Should().BeOfType<CdsCoveragePrepared>().Subject;
        prepared.Source.Should().Be(CdsCoverageSource.LocalRules);
        prepared.Decisions["ServiceRequest/sr-mri"]!.PaNeeded.Should().Be("auth-needed");
        prepared.Decisions["ServiceRequest/sr-2"].Should().BeNull();
        preparation.Coverage!.Decisions.Should().Be("covered/auth-needed/-");
        preparation.Coverage.RelayUsed.Should().BeFalse();
        relay.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task No_coverage_and_no_matching_payer_are_reported_without_rules()
    {
        var payer = new CdsPayer("Acme", ["Someone else"], null, true);
        var (engine, _) = Engine(payer, []);
        var noCoverage = OrderSign(ServiceRequest());
        noCoverage.Prefetch = new JsonObject { ["coverage"] = CdsTestSupport.SearchSet() };

        ((CdsCoveragePrepared)(await engine.PrepareAsync(Prep(noCoverage), CancellationToken.None)).Data!).Source.Should().Be(CdsCoverageSource.NoCoverage);
        ((CdsCoveragePrepared)(await engine.PrepareAsync(Prep(OrderSign(ServiceRequest())), CancellationToken.None)).Data!).Source.Should().Be(CdsCoverageSource.NoPayer);
    }

    [Fact]
    public async Task A_relay_payer_gets_the_call_without_the_ehr_token_by_default()
    {
        var payer = new CdsPayer("Acme", ["Acme Health"], null, true, relayEnabled: true, relayServiceUrl: "https://crd.acme.example/cds-services/crd");
        var (engine, relay) = Engine(payer, []);
        var request = WithAuthorization(OrderSign(ServiceRequest()));

        var preparation = await engine.PrepareAsync(Prep(request), CancellationToken.None);

        ((CdsCoveragePrepared)preparation.Data!).Source.Should().Be(CdsCoverageSource.Relay);
        var sent = relay.Sent.Should().ContainSingle().Subject;
        sent.FhirAuthorization.Should().BeNull();
        sent.FhirServer.Should().BeNull();
        sent.HookInstance.Should().NotBe(request.HookInstance);
        sent.Prefetch!["coverage"].Should().NotBeNull();
        preparation.Coverage!.RelayUsed.Should().BeTrue();
        preparation.Coverage.RelayOutcome.Should().Be("ok");
    }

    [Fact]
    public async Task Forwarding_the_ehr_token_happens_only_when_the_payer_allows_it()
    {
        var payer = new CdsPayer("Acme", ["Acme Health"], null, true, relayEnabled: true,
            relayServiceUrl: "https://crd.acme.example/cds-services/crd", forwardFhirAuthorization: true);
        var (engine, relay) = Engine(payer, []);

        await engine.PrepareAsync(Prep(WithAuthorization(OrderSign(ServiceRequest()))), CancellationToken.None);

        relay.Sent.Single().FhirAuthorization!.AccessToken.Should().Be("ehr-secret-token");
    }

    [Fact]
    public async Task A_failed_relay_falls_back_to_local_rules_when_allowed_and_otherwise_fails()
    {
        var failing = (CdsRequest _) => new CdsRelayResult(false, "timed out", null, 900, null);
        var withFallback = new CdsPayer("Acme", ["Acme Health"], null, true, relayEnabled: true, relayServiceUrl: "https://crd.acme.example/x");
        var (engine, _) = Engine(withFallback, [Rule(withFallback.Id, Values())], failing);

        var fallback = await engine.PrepareAsync(Prep(OrderSign(ServiceRequest())), CancellationToken.None);
        ((CdsCoveragePrepared)fallback.Data!).Source.Should().Be(CdsCoverageSource.LocalRules);
        fallback.Coverage!.RelayUsed.Should().BeTrue();
        fallback.Coverage.RelayOutcome.Should().Be("timed out; local rules used");

        var noFallback = new CdsPayer("Acme", ["Acme Health"], null, true, relayEnabled: true, relayServiceUrl: "https://crd.acme.example/x",
            fallbackToLocalRules: false);
        var (strict, _) = Engine(noFallback, [Rule(noFallback.Id, Values())], failing);
        var failed = await strict.PrepareAsync(Prep(OrderSign(ServiceRequest())), CancellationToken.None);
        ((CdsCoveragePrepared)failed.Data!).Source.Should().Be(CdsCoverageSource.RelayFailed);
    }

    [Fact]
    public async Task A_failed_relay_without_fallback_is_a_degraded_call_with_no_cards()
    {
        var payer = new CdsPayer("Acme", ["Acme Health"], null, true, relayEnabled: true, relayServiceUrl: "https://crd.acme.example/x",
            fallbackToLocalRules: false);
        var (engine, _) = Engine(payer, [], _ => new CdsRelayResult(false, "HTTP 503", 503, 30, null));
        var evaluator = new CdsEvaluator(SettingsCache(), CdsTestSupport.Options(), NullLogger<CdsEvaluator>.Instance, null, [engine]);
        var instance = new CdsServiceInstance("crd-test", "CRD", Handler.Key, CdsHookNames.OrderSign, "CRD.", true, null, "Segue", null, null);
        var issuer = new CdsTrustedIssuer("EHR", "Epic", "https://ehr.example.org/api/FHIR/R4", "https://ehr.example.org/jwks", null, null, true);

        var evaluation = await evaluator.EvaluateAsync(instance, Handler, OrderSign(ServiceRequest()), issuer, CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.CardCount.Should().Be(0);
        evaluation.Coverage!.RelayOutcome.Should().Be("HTTP 503");
    }

    [Fact]
    public async Task A_test_run_never_relays()
    {
        var payer = new CdsPayer("Acme", ["Acme Health"], null, true, relayEnabled: true, relayServiceUrl: "https://crd.acme.example/x");
        var (engine, relay) = Engine(payer, [Rule(payer.Id, Values())]);

        var preparation = await engine.PrepareAsync(Prep(OrderSign(ServiceRequest()), live: false), CancellationToken.None);

        relay.Sent.Should().BeEmpty();
        ((CdsCoveragePrepared)preparation.Data!).Source.Should().Be(CdsCoverageSource.LocalRules);
        preparation.Note.Should().Contain("Test run");
    }

    [Fact]
    public async Task End_to_end_through_the_evaluator_the_sample_order_gets_a_system_action()
    {
        var payer = new CdsPayer("Example Health Plan", ["EXAMPLE-PAYER"], null, true);
        var (engine, _) = Engine(payer, [Rule(payer.Id, Values())]);
        var evaluator = new CdsEvaluator(SettingsCache(), CdsTestSupport.Options(), NullLogger<CdsEvaluator>.Instance, null, [engine]);
        var instance = new CdsServiceInstance("crd-test", "CRD", Handler.Key, CdsHookNames.OrderSign, "CRD.", true, null, "Segue", null, null);

        var evaluation = await evaluator.EvaluateAsync(instance, Handler, Handler.BuildSampleRequest(CdsHookNames.OrderSign), CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Success);
        evaluation.Response!.SystemActions.Should().ContainSingle();
        evaluation.CoverageAssertionCount.Should().Be(1);
        evaluation.Coverage!.PayerName.Should().Be("Example Health Plan");
        evaluation.Response.Cards.Should().OnlyContain(card => card.Source.Label == "Segue");
    }

    // ---- Readiness ----

    private static FHIRBridge.Application.DTOs.Cds.CdsReadinessDto Readiness(
        IReadOnlyList<CdsServiceInstance> services, IReadOnlyList<CdsPayer> payers, IReadOnlyList<CdsCoverageRule> rules) =>
        FHIRBridge.Application.Services.Cds.CdsReadinessRules.Evaluate(
            new FHIRBridge.Application.Services.Cds.CdsReadinessInputs(
                "https://segue.example.org", false, true, [], services,
                new FHIRBridge.Application.Services.Cds.CdsServiceHandlerRegistry([Handler]),
                new FHIRBridge.Application.Abstractions.Licensing.LicenseStatus(
                    FHIRBridge.Application.Abstractions.Licensing.LicenseState.Active, "Hospital", null, null, null, null, [], null),
                600, 1, payers, rules),
            DateTime.UtcNow);

    [Fact]
    public void Readiness_has_no_crd_checks_without_crd_services_or_payers()
    {
        Readiness([], [], []).Checks.Should().NotContain(check => check.Key.StartsWith("crd-", StringComparison.Ordinal));
    }

    [Fact]
    public void Readiness_flags_empty_payers_expiring_rules_token_forwarding_and_missing_sign_off()
    {
        var service = new CdsServiceInstance("crd", "CRD", Handler.Key, CdsHookNames.OrderSign, "CRD.", true, null, "Segue", null, null);
        var empty = new CdsPayer("Empty Plan", ["EMPTY"], null, true);
        var forwarding = new CdsPayer("Relay Plan", ["RELAY"], null, true, relayEnabled: true,
            relayServiceUrl: "https://crd.relay.example/x", forwardFhirAuthorization: true);
        var withRules = new CdsPayer("Rules Plan", ["RULES"], null, true);
        var ending = Rule(withRules.Id, Values(to: Today.AddDays(10)));

        var checks = Readiness([service], [empty, forwarding, withRules], [ending]).Checks.ToDictionary(c => c.Key);

        checks["crd-payer-coverage"].Status.Should().Be("warning");
        checks["crd-payer-coverage"].Detail.Should().Contain("Empty Plan").And.NotContain("Relay Plan");
        checks["crd-relay-https"].Status.Should().Be("warning");
        checks["crd-rules-expiring"].Status.Should().Be("warning");
        checks["crd-sign-off"].Status.Should().Be("warning");

        var none = Readiness([service], [], []).Checks.Single(c => c.Key == "crd-payer-coverage");
        none.Status.Should().Be("blocker", "an enabled CRD service with no payer answers nothing");
    }

    [Fact]
    public void Readiness_does_not_count_rules_that_have_ended_or_not_started()
    {
        var service = new CdsServiceInstance("crd", "CRD", Handler.Key, CdsHookNames.OrderSign, "CRD.", true, null, "Segue", null, null);
        var expired = new CdsPayer("Expired Plan", ["EXPIRED"], null, true);
        var future = new CdsPayer("Future Plan", ["FUTURE"], null, true);
        var current = new CdsPayer("Current Plan", ["CURRENT"], null, true);
        var rules = new[]
        {
            Rule(expired.Id, Values(from: Today.AddDays(-60), to: Today.AddDays(-1))),
            Rule(future.Id, Values(from: Today.AddDays(5))),
            Rule(current.Id, Values(from: Today.AddDays(-5))),
        };

        var check = Readiness([service], [expired, future, current], rules).Checks.Single(c => c.Key == "crd-payer-coverage");

        check.Status.Should().Be("warning");
        check.Detail.Should().Contain("Expired Plan").And.Contain("Future Plan").And.NotContain("Current Plan");
        Readiness([service], [], []).Checks.Single(c => c.Key == "crd-payer-coverage").HowToFix.Should().Contain("Coverage (CRD)", "the hint names the portal tab");
    }

    private static FHIRBridge.Application.Abstractions.Caching.ISystemSettingsCache SettingsCache()
    {
        var settings = new Moq.Mock<FHIRBridge.Application.Abstractions.Caching.ISystemSettingsCache>();
        settings.Setup(s => s.GetIntAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string _, int fallback, CancellationToken _) => Task.FromResult(fallback));
        return settings.Object;
    }
}
