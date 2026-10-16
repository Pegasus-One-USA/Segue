using System.Globalization;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The five built-in handler types against their own sample requests (a card), negative cases (no card),
/// malformed input (never throws, no cards) and their settings. The rule data is SAMPLE data; these tests pin
/// behaviour, not clinical correctness.
/// </summary>
public sealed class CdsHandlerTests
{
    private const string RxNorm = CdsSampleReferenceData.RxNormSystem;

    public static TheoryData<string, string> HandlerHooks()
    {
        var data = new TheoryData<string, string>();
        foreach (var handler in AllHandlers())
        {
            foreach (var hook in handler.SupportedHooks)
            {
                data.Add(handler.Key, hook);
            }
        }

        return data;
    }

    private static ICdsServiceHandler[] AllHandlers() =>
    [
        new DrugAllergyCdsHandler(),
        new DrugInteractionCdsHandler(),
        new RenalDoseCdsHandler(),
        new CareGapCdsHandler(),
        new PriorAuthCdsHandler(),
    ];

    private static ICdsServiceHandler Handler(string key) => AllHandlers().Single(h => h.Key == key);

    private static async Task<CdsHandlerResult> RunAsync(ICdsServiceHandler handler, CdsRequest request, string? settingsJson = null) =>
        await handler.EvaluateAsync(CdsTestSupport.Context(handler, request, settingsJson), CancellationToken.None);

    // ---- Every handler, every hook ----

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public async Task The_sample_request_produces_at_least_one_valid_card(string key, string hook)
    {
        var handler = Handler(key);
        var request = handler.BuildSampleRequest(hook);

        var result = await RunAsync(handler, request);

        request.Hook.Should().Be(hook);
        result.DegradedReason.Should().BeNull();
        result.Cards.Should().NotBeEmpty();
        result.Cards.Should().AllSatisfy(card =>
        {
            card.Summary.Length.Should().BeInRange(1, CdsCard.SummaryMaxLength);
            CdsIndicator.All.Should().Contain(card.Indicator);
            card.Source.Label.Should().Be("Segue test");
            Guid.TryParse(card.Uuid, out _).Should().BeTrue();
            card.Detail.Should().Contain(CdsSampleReferenceData.CardNotice);
        });
    }

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public void The_sample_request_carries_every_prefetch_key_the_handler_advertises(string key, string hook)
    {
        var handler = Handler(key);

        var request = handler.BuildSampleRequest(hook);

        foreach (var prefetchKey in handler.GetPrefetchTemplates(hook).Keys)
        {
            request.Prefetch.Should().NotBeNull();
            request.Prefetch!.ContainsKey(prefetchKey).Should().BeTrue($"{key} advertises '{prefetchKey}'");
        }
    }

    public static TheoryData<string> MalformedRequests() =>
    [
        """{"hook":"order-sign","hookInstance":"x","context":{}}""",
        """{"hook":"order-sign","hookInstance":"x","context":{"draftOrders":"not-a-bundle","selections":7},"prefetch":{"allergies":42,"medications":"x","egfr":[1,2],"patient":true,"conditions":{"resourceType":"Bundle","entry":"nope"}}}""",
        """{"hook":"order-sign","hookInstance":"x","context":{"draftOrders":{"resourceType":"Bundle","entry":[null,1,{"resource":null},{"resource":{"resourceType":"MedicationRequest","medicationCodeableConcept":{"coding":"bad","text":5}}}]}},"prefetch":{"allergies":{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"AllergyIntolerance","code":[]}}]}}}""",
        """{"hook":"patient-view","hookInstance":"x","context":{"patientId":"p"},"prefetch":{"patient":{"resourceType":"Patient","birthDate":"not-a-date"},"hba1c":{"resourceType":"Observation","code":{"coding":[{"code":"4548-4"}]},"effectiveDateTime":12},"immunizations":{"resourceType":"Bundle"},"procedures":null}}""",
        """{"hook":"order-select","hookInstance":"x","context":{"selections":[null,"MedicationRequest/1"],"draftOrders":{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"ServiceRequest","code":{"concept":{"coding":[{"system":null,"code":null}]}}}}]}},"prefetch":{"egfr":{"resourceType":"Observation","code":{"coding":[{"code":"62238-1"}]},"valueQuantity":{"value":"25"}}}}""",
    ];

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task Malformed_input_never_throws_and_yields_no_cards(string json)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<CdsRequest>(json)!;

        foreach (var handler in AllHandlers())
        {
            var cards = await FluentActions.Awaiting(() => RunAsync(handler, request)).Should().NotThrowAsync();
            if (handler.Key != CdsHandlerKeys.CareGap)
            {
                // Care-gap reminds when nothing is on record, so "no data" may legitimately yield reminders.
                cards.Subject.Cards.Should().BeEmpty($"{handler.Key} found nothing it can read");
            }
        }
    }

    [Fact]
    public async Task A_cancelled_token_is_honoured()
    {
        var handler = new DrugAllergyCdsHandler();
        var context = CdsTestSupport.Context(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign));

        var act = () => handler.EvaluateAsync(context, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- drug-allergy ----

    [Fact]
    public async Task Drug_allergy_flags_a_penicillin_allergy_against_amoxicillin_as_critical_when_high_criticality()
    {
        var handler = new DrugAllergyCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign));

        var card = result.Cards.Should().ContainSingle().Subject;
        card.Indicator.Should().Be(CdsIndicator.Critical);
        card.Summary.Should().Contain("amoxicillin");
        card.OverrideReasons.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Drug_allergy_gives_no_card_when_the_order_does_not_match_any_allergy()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-1", "5640", "ibuprofen 400 MG Oral Tablet")],
            new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(Allergy("91936005", "Allergy to penicillin")) });

        var result = await RunAsync(new DrugAllergyCdsHandler(), request);

        result.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_allergy_uses_the_configured_indicator()
    {
        var handler = new DrugAllergyCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign), """{"highCriticalityIndicator":"info"}""");

        result.Cards.Single().Indicator.Should().Be(CdsIndicator.Info);
    }

    [Fact]
    public async Task Drug_allergy_ignores_an_inactive_allergy_unless_configured_to_include_it()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-1", "723", "amoxicillin 500 MG Oral Capsule")],
            new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(Allergy("91936005", "Allergy to penicillin", clinicalStatus: "inactive")) });
        var handler = new DrugAllergyCdsHandler();

        (await RunAsync(handler, request)).Cards.Should().BeEmpty();
        (await RunAsync(handler, request, """{"includeInactiveAllergies":true}""")).Cards.Should().ContainSingle();
    }

    [Fact]
    public async Task Drug_allergy_ignores_a_refuted_allergy()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-1", "723", "amoxicillin 500 MG Oral Capsule")],
            new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(Allergy("91936005", "Allergy to penicillin", verificationStatus: "refuted")) });

        (await RunAsync(new DrugAllergyCdsHandler(), request)).Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_allergy_cross_reactivity_card_can_be_switched_off()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-1", "2231", "cephalexin 500 MG Oral Capsule")],
            new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(Allergy("91936005", "Allergy to penicillin")) });
        var handler = new DrugAllergyCdsHandler();

        var withCheck = await RunAsync(handler, request);
        var withoutCheck = await RunAsync(handler, request, """{"checkCrossReactivity":false}""");

        withCheck.Cards.Should().ContainSingle().Which.Summary.Should().Contain("cross-reactivity");
        withCheck.Cards.Single().Indicator.Should().Be(CdsIndicator.Info);
        withoutCheck.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_allergy_on_order_select_checks_only_the_selected_orders()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSelect,
            [
                MedicationOrder("draft-ibuprofen", "5640", "ibuprofen 400 MG Oral Tablet"),
                MedicationOrder("draft-amoxicillin", "723", "amoxicillin 500 MG Oral Capsule"),
            ],
            new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(Allergy("91936005", "Allergy to penicillin")) });
        request.Context!["selections"] = new JsonArray("MedicationRequest/draft-ibuprofen");

        (await RunAsync(new DrugAllergyCdsHandler(), request)).Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_allergy_degrades_when_the_allergy_prefetch_is_null()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-1", "723", "amoxicillin 500 MG Oral Capsule")],
            new JsonObject { ["allergies"] = null });

        var result = await RunAsync(new DrugAllergyCdsHandler(), request);

        result.Cards.Should().BeEmpty();
        result.DegradedReason.Should().NotBeNullOrWhiteSpace();
    }

    // ---- drug-interaction ----

    [Fact]
    public async Task Drug_interaction_flags_warfarin_with_ibuprofen_as_a_major_warning()
    {
        var handler = new DrugInteractionCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign));

        var card = result.Cards.Should().ContainSingle().Subject;
        card.Indicator.Should().Be(CdsIndicator.Warning);
        card.Summary.Should().StartWith("Major interaction");
    }

    [Fact]
    public async Task Drug_interaction_respects_the_minimum_severity()
    {
        var handler = new DrugInteractionCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign), """{"minimumSeverity":"contraindicated"}""");

        result.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_interaction_uses_the_configured_indicator_per_severity()
    {
        var handler = new DrugInteractionCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign), """{"majorIndicator":"critical"}""");

        result.Cards.Single().Indicator.Should().Be(CdsIndicator.Critical);
    }

    [Fact]
    public async Task Drug_interaction_checks_drafted_orders_against_each_other_only_when_enabled()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [
                MedicationOrder("draft-sildenafil", "136411", "sildenafil 50 MG Oral Tablet"),
                MedicationOrder("draft-nitro", "4917", "nitroglycerin 0.4 MG Sublingual Tablet"),
            ],
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet() });
        var handler = new DrugInteractionCdsHandler();

        var within = await RunAsync(handler, request);
        var notWithin = await RunAsync(handler, request, """{"checkWithinDraftOrders":false}""");

        within.Cards.Should().ContainSingle("the pair is reported once, not once per side")
            .Which.Indicator.Should().Be(CdsIndicator.Critical);
        notWithin.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_interaction_ignores_stopped_medications()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-ibuprofen", "5640", "ibuprofen 400 MG Oral Tablet")],
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet(MedicationOrder("old-warfarin", "11289", "warfarin 5 MG", status: "stopped")) });

        (await RunAsync(new DrugInteractionCdsHandler(), request)).Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_interaction_gives_no_card_for_an_unrelated_pair()
    {
        var request = OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-amoxicillin", "723", "amoxicillin 500 MG Oral Capsule")],
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet(MedicationOrder("active-metformin", "6809", "metformin 500 MG", status: "active")) });

        (await RunAsync(new DrugInteractionCdsHandler(), request)).Cards.Should().BeEmpty();
    }

    // ---- renal-dose ----

    [Fact]
    public async Task Renal_dose_says_avoid_metformin_at_egfr_25()
    {
        var handler = new RenalDoseCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign));

        var card = result.Cards.Should().ContainSingle().Subject;
        card.Indicator.Should().Be(CdsIndicator.Critical);
        card.Summary.Should().StartWith("Avoid");
    }

    [Fact]
    public async Task Renal_dose_asks_for_a_dose_review_between_the_review_and_avoid_thresholds()
    {
        var result = await RunAsync(new RenalDoseCdsHandler(), RenalRequest(egfr: 40, "861007", "metformin hydrochloride 500 MG Oral Tablet"));

        var card = result.Cards.Should().ContainSingle().Subject;
        card.Indicator.Should().Be(CdsIndicator.Warning);
        card.Summary.Should().StartWith("Renal dose review");
    }

    [Fact]
    public async Task Renal_dose_respects_the_egfr_threshold_setting()
    {
        var result = await RunAsync(new RenalDoseCdsHandler(), RenalRequest(egfr: 25, "861007", "metformin hydrochloride 500 MG Oral Tablet"), """{"egfrThreshold":20}""");

        result.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Renal_dose_can_ignore_the_drug_own_threshold()
    {
        var request = RenalRequest(egfr: 50, "861007", "metformin hydrochloride 500 MG Oral Tablet");
        var handler = new RenalDoseCdsHandler();

        (await RunAsync(handler, request)).Cards.Should().BeEmpty("metformin's sample review threshold is 45");
        (await RunAsync(handler, request, """{"useDrugThresholds":false}""")).Cards.Should().ContainSingle();
    }

    [Fact]
    public async Task Renal_dose_treats_an_old_result_as_missing()
    {
        var request = RenalRequest(egfr: 25, "861007", "metformin hydrochloride 500 MG Oral Tablet", daysAgo: 400);
        var handler = new RenalDoseCdsHandler();

        var defaultAge = await RunAsync(handler, request);
        var longerAge = await RunAsync(handler, request, """{"maxResultAgeDays":1000}""");

        defaultAge.Cards.Should().BeEmpty();
        defaultAge.DegradedReason.Should().Contain("older");
        longerAge.Cards.Should().ContainSingle();
    }

    [Fact]
    public async Task Renal_dose_ignores_drugs_that_are_not_renally_listed()
    {
        var result = await RunAsync(new RenalDoseCdsHandler(), RenalRequest(egfr: 10, "5640", "ibuprofen 400 MG Oral Tablet"));

        result.Cards.Should().BeEmpty();
    }

    // ---- care-gap ----

    [Fact]
    public async Task Care_gap_sample_gives_three_info_reminders()
    {
        var handler = new CareGapCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.PatientView));

        result.Cards.Should().HaveCount(3);
        result.Cards.Should().AllSatisfy(card => card.Indicator.Should().Be(CdsIndicator.Info));
    }

    [Theory]
    [InlineData("""{"checkHba1c":false}""", "HbA1c")]
    [InlineData("""{"hba1cIntervalMonths":12}""", "HbA1c")]
    [InlineData("""{"checkInfluenza":false}""", "Influenza")]
    [InlineData("""{"influenzaIntervalMonths":24}""", "Influenza")]
    [InlineData("""{"checkColorectalScreening":false}""", "Colorectal")]
    [InlineData("""{"colorectalAgeFrom":65}""", "Colorectal")]
    public async Task Care_gap_settings_switch_off_or_relax_one_reminder(string settings, string droppedReminder)
    {
        var handler = new CareGapCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.PatientView), settings);

        result.Cards.Should().HaveCount(2);
        result.Cards.Should().NotContain(card => card.Summary.StartsWith(droppedReminder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Care_gap_uses_the_configured_indicator()
    {
        var handler = new CareGapCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.PatientView), """{"indicator":"warning"}""");

        result.Cards.Should().AllSatisfy(card => card.Indicator.Should().Be(CdsIndicator.Warning));
    }

    [Fact]
    public async Task Care_gap_gives_no_card_when_everything_is_up_to_date()
    {
        var request = new CdsRequest
        {
            Hook = CdsHookNames.PatientView,
            HookInstance = Guid.NewGuid().ToString(),
            Context = new JsonObject { ["patientId"] = "p-1" },
            Prefetch = new JsonObject
            {
                ["patient"] = new JsonObject { ["resourceType"] = "Patient", ["id"] = "p-1", ["birthDate"] = DateText(60 * 365) },
                ["conditions"] = CdsTestSupport.SearchSet(new JsonObject
                {
                    ["resourceType"] = "Condition",
                    ["code"] = Concept(CdsSampleReferenceData.SnomedSystem, "44054006", "Diabetes mellitus type 2"),
                }),
                ["hba1c"] = CdsTestSupport.SearchSet(new JsonObject
                {
                    ["resourceType"] = "Observation",
                    ["status"] = "final",
                    ["code"] = Concept(CdsSampleReferenceData.LoincSystem, "4548-4", "HbA1c"),
                    ["effectiveDateTime"] = DateText(30),
                }),
                ["immunizations"] = CdsTestSupport.SearchSet(new JsonObject
                {
                    ["resourceType"] = "Immunization",
                    ["status"] = "completed",
                    ["vaccineCode"] = Concept(CdsSampleReferenceData.CvxSystem, "158", "influenza, injectable, quadrivalent"),
                    ["occurrenceDateTime"] = DateText(30),
                }),
                ["procedures"] = CdsTestSupport.SearchSet(new JsonObject
                {
                    ["resourceType"] = "Procedure",
                    ["status"] = "completed",
                    ["code"] = Concept(CdsSampleReferenceData.SnomedSystem, "73761001", "Colonoscopy"),
                    ["performedDateTime"] = DateText(365),
                }),
            },
        };

        (await RunAsync(new CareGapCdsHandler(), request)).Cards.Should().BeEmpty();
    }

    // ---- prior-auth ----

    [Fact]
    public async Task Prior_auth_flags_an_mri_on_the_default_list()
    {
        var handler = new PriorAuthCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign));

        var card = result.Cards.Should().ContainSingle().Subject;
        card.Indicator.Should().Be(CdsIndicator.Warning);
        card.Detail.Should().Contain("70553");
        card.Links.Should().BeNull("no submission URL is configured by default");
    }

    [Theory]
    [InlineData("""{"codes":["99999"]}""")]
    [InlineData("""{"codes":[]}""")]
    [InlineData("""{"codes":["http://example.org/other|70553"]}""")]
    public async Task Prior_auth_gives_no_card_when_the_order_is_not_on_the_configured_list(string settings)
    {
        var handler = new PriorAuthCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign), settings);

        result.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Prior_auth_matches_a_bare_code_and_shows_payer_and_portal_link()
    {
        var handler = new PriorAuthCdsHandler();

        var result = await RunAsync(
            handler,
            handler.BuildSampleRequest(CdsHookNames.OrderSign),
            """{"codes":["70553"],"payerName":"Acme Health","submissionUrl":"https://payer.example.org/pa","indicator":"critical"}""");

        var card = result.Cards.Should().ContainSingle().Subject;
        card.Indicator.Should().Be(CdsIndicator.Critical);
        card.Detail.Should().Contain("Acme Health");
        card.Links.Should().ContainSingle().Which.Url.Should().Be("https://payer.example.org/pa");
    }

    [Fact]
    public async Task Prior_auth_ignores_a_non_http_portal_link()
    {
        var handler = new PriorAuthCdsHandler();

        var result = await RunAsync(handler, handler.BuildSampleRequest(CdsHookNames.OrderSign), """{"submissionUrl":"javascript:alert(1)"}""");

        result.Cards.Single().Links.Should().BeNull();
    }

    // ---- Builders (synthetic data only) ----

    private static CdsRequest OrderRequest(string hook, IReadOnlyList<JsonObject> orders, JsonObject? prefetch) => new()
    {
        Hook = hook,
        HookInstance = Guid.NewGuid().ToString(),
        Context = new JsonObject
        {
            ["userId"] = "Practitioner/u-1",
            ["patientId"] = "p-1",
            ["draftOrders"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(orders.Select(order => (JsonNode?)new JsonObject { ["resource"] = order }).ToArray()),
            },
        },
        Prefetch = prefetch,
    };

    private static CdsRequest RenalRequest(double egfr, string rxNormCode, string display, int daysAgo = 10) =>
        OrderRequest(
            CdsHookNames.OrderSign,
            [MedicationOrder("draft-1", rxNormCode, display)],
            new JsonObject
            {
                ["egfr"] = CdsTestSupport.SearchSet(new JsonObject
                {
                    ["resourceType"] = "Observation",
                    ["status"] = "final",
                    ["code"] = Concept(CdsSampleReferenceData.LoincSystem, "62238-1", "eGFR CKD-EPI"),
                    ["effectiveDateTime"] = DateText(daysAgo),
                    ["valueQuantity"] = new JsonObject { ["value"] = egfr, ["unit"] = "mL/min/{1.73_m2}" },
                }),
            });

    private static JsonObject MedicationOrder(string id, string rxNormCode, string display, string status = "draft") => new()
    {
        ["resourceType"] = "MedicationRequest",
        ["id"] = id,
        ["status"] = status,
        ["intent"] = "order",
        ["medicationCodeableConcept"] = Concept(RxNorm, rxNormCode, display),
        ["subject"] = new JsonObject { ["reference"] = "Patient/p-1" },
    };

    private static JsonObject Allergy(string snomedCode, string display, string clinicalStatus = "active", string verificationStatus = "confirmed") => new()
    {
        ["resourceType"] = "AllergyIntolerance",
        ["clinicalStatus"] = Concept("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", clinicalStatus, clinicalStatus),
        ["verificationStatus"] = Concept("http://terminology.hl7.org/CodeSystem/allergyintolerance-verification", verificationStatus, verificationStatus),
        ["code"] = Concept(CdsSampleReferenceData.SnomedSystem, snomedCode, display),
        ["patient"] = new JsonObject { ["reference"] = "Patient/p-1" },
    };

    private static JsonObject Concept(string system, string code, string display) => new()
    {
        ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code, ["display"] = display }),
        ["text"] = display,
    };

    private static string DateText(int daysAgo) =>
        DateTime.UtcNow.Date.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
