using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The Phase 1b / 2 contracts: per-instance fixes, dismiss reasons and links (domain rules and JSON round trip),
/// trusted issuer jku / fhirServer matching, Epic request metadata and feedback shapes, template resolution and
/// suggestion rules.
/// </summary>
public sealed class CdsEpicContractTests
{
    private static CdsServiceInstance Instance(
        CdsFixSettings? fixes = null,
        IEnumerable<CdsOverrideReasonOption>? reasons = null,
        IEnumerable<CdsCardLinkOption>? links = null,
        bool fetchMissingData = true) =>
        new("drug-allergy-check", "Title", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Description", true, null, "Label", null, null,
            fetchMissingData, fixes, reasons, links);

    [Fact]
    public void A_new_instance_fetches_missing_data_has_fixes_off_default_reasons_and_no_links()
    {
        var instance = Instance();

        instance.FetchMissingData.Should().BeTrue();
        instance.FixesJson.Should().BeNull();
        instance.FixesEnabled.Should().BeFalse();
        instance.GetOverrideReasons().Should().BeEquivalentTo(CdsOverrideReasonOption.Defaults);
        instance.GetOwnOverrideReasons().Should().BeNull();
        instance.GetLinks().Should().BeEmpty();
    }

    [Fact]
    public void Fix_settings_round_trip_through_json()
    {
        var fixes = new CdsFixSettings(
            true,
            [
                new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, []),
                new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
                    [new CdsFixCodeMapping("penicillin", CdsFixCodeSystems.RxNormOid, " 18631 ", "azithromycin")]),
            ],
            CdsFixTypeKeys.RemoveDraftOrder);

        var instance = Instance(fixes);

        instance.FixesEnabled.Should().BeTrue();
        var read = instance.GetFixes();
        read.RecommendedFixType.Should().Be(CdsFixTypeKeys.RemoveDraftOrder);
        read.EnabledFixType(CdsFixTypeKeys.AddAlternativeMedication)!.Mappings.Single().Code.Should().Be("18631");
    }

    [Fact]
    public void The_master_switch_alone_does_not_count_as_fixes_on()
    {
        var instance = Instance(new CdsFixSettings(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, false, [])]));

        instance.FixesEnabled.Should().BeFalse("no fix type is on, so no card can change an EHR draft");
    }

    [Fact]
    public void Created_orders_carry_a_category()
    {
        var mapping = new CdsFixCodeMapping(null, CdsFixCodeSystems.RxNormOid, "18631", "azithromycin");

        var medication = CdsFixResources.MedicationRequest(mapping, "p1");
        var service = CdsFixResources.ServiceRequest(mapping, "p1");

        medication["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("outpatient");
        service["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("108252007");
        medication["status"]!.GetValue<string>().Should().Be("draft");
    }

    [Fact]
    public void A_recommended_fix_must_be_switched_on()
    {
        var fixes = new CdsFixSettings(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, false, [])], CdsFixTypeKeys.RemoveDraftOrder);

        var act = () => Instance(fixes);

        act.Should().Throw<BusinessRuleException>();
    }

    [Theory]
    [InlineData("bad code")]
    [InlineData("")]
    [InlineData("x/y")]
    public void Dismiss_reason_codes_are_alphanumeric_dash_underscore(string code)
    {
        var act = () => Instance(reasons: [new CdsOverrideReasonOption(code, "Text")]);

        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void At_most_ten_dismiss_reasons()
    {
        var act = () => Instance(reasons: Enumerable.Range(1, 11).Select(i => new CdsOverrideReasonOption($"r{i}", "Text")));

        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void Own_dismiss_reasons_replace_the_defaults()
    {
        var instance = Instance(reasons: [new CdsOverrideReasonOption("pt_declined", "Patient declined")]);

        instance.GetOverrideReasons().Should().ContainSingle().Which.Code.Should().Be("pt_declined");
    }

    [Fact]
    public void Links_must_be_https_and_smart_only_fields_need_a_smart_link()
    {
        ((Action)(() => Instance(links: [new CdsCardLinkOption("Guide", "http://example.org", CdsCardLinkTypes.Absolute)])))
            .Should().Throw<BusinessRuleException>();
        ((Action)(() => Instance(links: [new CdsCardLinkOption("Guide", "https://example.org", CdsCardLinkTypes.Absolute, "ctx")])))
            .Should().Throw<BusinessRuleException>();
        ((Action)(() => Instance(links: [new CdsCardLinkOption("Guide", "https://example.org", CdsCardLinkTypes.Absolute, null, true)])))
            .Should().Throw<BusinessRuleException>();

        var instance = Instance(links: [new CdsCardLinkOption("App", "https://app.example.org/launch", "SMART", "ctx", true)]);
        instance.GetLinks().Single().Type.Should().Be(CdsCardLinkTypes.Smart);
    }

    [Fact]
    public void Issuer_jku_and_fhir_server_are_matched_after_normalization()
    {
        var issuer = new CdsTrustedIssuer(
            "Epic", "Epic", "https://ehr.example.org/api/FHIR/R4", "https://ehr.example.org/jwks.json", null, null, true,
            expectedSubject: "client-1");

        issuer.AllowCallback.Should().BeTrue();
        issuer.IsTrustedJku("https://EHR.example.org:443/jwks.json/").Should().BeTrue();
        issuer.IsTrustedJku("https://evil.example.org/jwks.json").Should().BeFalse();
        issuer.IsTrustedFhirServer("https://ehr.example.org/api/FHIR/R4/").Should().BeTrue();
        issuer.IsTrustedFhirServer("https://ehr.example.org/api/FHIR/DSTU2").Should().BeFalse();
    }

    [Fact]
    public void A_fhir_base_url_is_http_or_https_without_query_or_fragment()
    {
        // http is stored (a dev FHIR server); the callback client refuses it unless Cds:AllowInsecureCallback is on.
        new CdsTrustedIssuer("Dev", "Other", "iss", "https://ehr/jwks", null, null, true, fhirBaseUrl: "http://hapi:8080/fhir/")
            .FhirBaseUrl.Should().Be("http://hapi:8080/fhir");

        foreach (var bad in new[] { "ftp://ehr/fhir", "https://ehr/fhir?x=1", "https://ehr/fhir#a", "not a url" })
        {
            var act = () => new CdsTrustedIssuer("Epic", "Epic", "iss", "https://ehr/jwks", null, null, true, fhirBaseUrl: bad);
            act.Should().Throw<BusinessRuleException>(bad);
        }
    }

    [Fact]
    public void Epic_request_extensions_are_read_as_short_tokens_only()
    {
        var request = JsonSerializer.Deserialize<CdsRequest>("""
            {"hook":"order-select","hookInstance":"x","context":{},
             "extension":{"com.epic.cdshooks.request.bpa-trigger-action":60,
                          "com.epic.cdshooks.request.epic-version":"11.2",
                          "com.epic.cdshooks.request.criteria-id":"<script>"}}
            """)!;

        var metadata = CdsRequestMetadata.Read(request);

        metadata.TriggerAction.Should().Be("60");
        metadata.EpicVersion.Should().Be("11.2");
        metadata.CriteriaId.Should().BeNull();
    }

    [Fact]
    public void Feedback_accepts_epic_plural_override_reasons_and_the_category_extension()
    {
        var feedback = JsonSerializer.Deserialize<CdsFeedbackRequest>("""
            {"feedback":[
              {"card":"c1","outcome":"overridden","outcomeTimestamp":"2026-01-01T00:00:00Z",
               "overrideReasons":{"extension":{"com.epic.cdshooks.feedback.overrideReason.reasonCategory":"45"},"userComment":"secret"}},
              {"card":"c2","outcome":"overridden","outcomeTimestamp":"2026-01-01T00:00:00Z",
               "overrideReason":{"reason":{"code":"will-monitor","system":"urn:segue:cds:override-reason"}}},
              {"card":"c3","outcome":"accepted","outcomeTimestamp":"2026-01-01T00:00:00Z","acceptedSuggestions":[{"id":"s1"}]}]}
            """)!;

        feedback.Feedback[0].EffectiveOverrideReason!.RecordableCode().Should().Be("45");
        feedback.Feedback[1].EffectiveOverrideReason!.RecordableCode().Should().Be("will-monitor");
        feedback.Feedback[2].AcceptedSuggestions!.Single().Id.Should().Be("s1");
    }

    [Fact]
    public void Prefetch_templates_resolve_against_the_context()
    {
        var context = new JsonObject { ["patientId"] = "abc 1" };

        CdsPrefetchTemplate.Resolve("AllergyIntolerance?patient={{context.patientId}}", context)
            .Should().Be("AllergyIntolerance?patient=abc%201");
        CdsPrefetchTemplate.Resolve("Encounter/{{context.encounterId}}", context).Should().BeNull();
    }

    [Fact]
    public void Suggestions_are_offered_only_for_switched_on_fixes_and_meet_the_rules()
    {
        var fixes = new CdsFixSettings(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, [])], CdsFixTypeKeys.RemoveDraftOrder);
        var context = new CdsHandlerContext(
            CdsInstanceSnapshot.From(Instance(fixes)),
            new CdsRequest { Hook = CdsHookNames.OrderSign, HookInstance = "x", Context = [] },
            CdsSettingsValues.Resolve([], null));

        var delete = context.CreateDeleteSuggestion(CdsFixTypeKeys.RemoveDraftOrder, "Remove", "MedicationRequest/m1", "Remove amoxicillin");
        var off = context.CreateCreateSuggestion(CdsFixTypeKeys.AddAlternativeMedication, "Add", new JsonObject(), "Add");
        var card = context.CreateCard("Summary", CdsIndicator.Warning, topic: CdsTopic.Create("DrugAllergyPenicillin"), suggestions: [delete, off]);

        off.Should().BeNull();
        delete!.IsRecommended.Should().BeTrue();
        card.Suggestions.Should().ContainSingle();
        card.SelectionBehavior.Should().Be(CdsSelectionBehavior.Any);
        CdsSuggestionRules.Validate(delete, new HashSet<string> { "MedicationRequest/m1" }).Should().BeNull();
        CdsSuggestionRules.Validate(delete, new HashSet<string>()).Should().NotBeNull();
        CdsSuggestionRules.Validate(
            new CdsSuggestion
            {
                Label = "Add",
                Uuid = "u",
                Actions = [new CdsAction { Type = CdsActionTypes.Create, Resource = new JsonObject { ["resourceType"] = "MedicationRequest", ["status"] = "active" } }],
            },
            new HashSet<string>()).Should().NotBeNull();
    }

    [Fact]
    public void Topic_codes_are_alphanumeric()
    {
        CdsTopic.IsValidCode("CareGapHbA1c").Should().BeTrue();
        CdsTopic.IsValidCode("care-gap").Should().BeFalse();
        CdsTopic.IsValidCode(new string('a', 101)).Should().BeFalse();
    }
}
