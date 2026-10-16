using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The built-in handlers against requests in the shape Epic sends (Phase 1b): a MedicationRequest with a
/// medicationReference resolved through a draftOrders Bundle entry, an absolute URL or a contained Medication, the
/// display-text fallback, STU3 ProcedureRequest and order-select selections; then the cards' static topic codes,
/// per-instance dismiss reasons and links, and one-click fixes (Phase 2b) end to end through the evaluator.
/// </summary>
public sealed class CdsEpicHandlerTests
{
    private const string FhirBase = "https://ehr.example.org/api/FHIR/R4";
    private const string PatientId = "epic-patient-1";
    private const string RxNorm = CdsSampleReferenceData.RxNormSystem;

    public enum MedShape
    {
        /// <summary>"Medication/id", the Medication its own draftOrders entry (Epic since Feb 2024).</summary>
        BundleEntry,

        /// <summary>An absolute reference matching the Medication entry's fullUrl.</summary>
        AbsoluteReference,

        /// <summary>"#id" to a contained Medication.</summary>
        Contained,

        /// <summary>No Medication anywhere: only the reference's upper-case display text.</summary>
        DisplayOnly,

        /// <summary>The Medication carries only an RxNorm code in its OID form, and the reference has no display.</summary>
        OidCodeOnly,
    }

    public static TheoryData<MedShape> Shapes() => new(Enum.GetValues<MedShape>());

    private static ICdsServiceHandler[] AllHandlers() =>
    [
        new DrugAllergyCdsHandler(),
        new DrugInteractionCdsHandler(),
        new RenalDoseCdsHandler(),
        new CareGapCdsHandler(),
        new PriorAuthCdsHandler(),
    ];

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

    private static ICdsServiceHandler Handler(string key) => AllHandlers().Single(h => h.Key == key);

    private static CdsServiceInstance Instance(
        ICdsServiceHandler handler,
        string hook,
        CdsFixSettings? fixes = null,
        IEnumerable<CdsOverrideReasonOption>? reasons = null,
        IEnumerable<CdsCardLinkOption>? links = null,
        string? settingsJson = null) =>
        new($"{handler.Key}-svc", "Title", handler.Key, hook, "Description", true, settingsJson, "Segue test", null, null,
            true, fixes, reasons, links);

    private static async Task<IReadOnlyList<CdsCard>> RunAsync(ICdsServiceHandler handler, CdsRequest request)
    {
        var result = await handler.EvaluateAsync(CdsTestSupport.Context(handler, request), CancellationToken.None);
        result.DegradedReason.Should().BeNull();
        return result.Cards;
    }

    /// <summary>The full path (evaluator: request checks, handler, card sanitizing) for a configured instance.</summary>
    private static async Task<IReadOnlyList<CdsCard>> EvaluateAsync(CdsServiceInstance instance, ICdsServiceHandler handler, CdsRequest request)
    {
        var evaluation = await CdsTestSupport.Evaluator().EvaluateAsync(instance, handler, request, CancellationToken.None);
        evaluation.Status.Should().Be(CdsCallStatus.Ok, evaluation.Detail);
        return evaluation.Response!.Cards;
    }

    // ---- Epic-shaped drafts, per drug check ----

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Drug_allergy_reads_an_epic_draft_medication(MedShape shape)
    {
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign,
            DraftMedication(shape, "draft-amox", "amoxicillin", "723"),
            new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(PenicillinAllergy()) });

        var cards = await RunAsync(new DrugAllergyCdsHandler(), request);

        cards.Should().ContainSingle("one draft order; the Medication entry beside it is not an order of its own")
            .Which.Indicator.Should().Be(CdsIndicator.Critical);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Drug_interaction_reads_an_epic_draft_medication(MedShape shape)
    {
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign,
            DraftMedication(shape, "draft-ibu", "ibuprofen", "5640"),
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet(ActiveContainedMedication("active-warf", "11289", "WARFARIN SODIUM 5 MG PO TABS")) });

        var cards = await RunAsync(new DrugInteractionCdsHandler(), request);

        cards.Should().ContainSingle().Which.Summary.Should().StartWith("Major interaction");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Renal_dose_reads_an_epic_draft_medication(MedShape shape)
    {
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign,
            DraftMedication(shape, "draft-met", "metformin", "6809"),
            new JsonObject { ["egfr"] = CdsTestSupport.SearchSet(Egfr(25)) });

        var cards = await RunAsync(new RenalDoseCdsHandler(), request);

        cards.Should().ContainSingle().Which.Summary.Should().StartWith("Avoid");
    }

    [Fact]
    public async Task An_active_medication_referenced_from_prefetch_is_resolved_through_the_prefetch_bundle()
    {
        var medication = new JsonObject
        {
            ["resourceType"] = "Medication",
            ["id"] = "med-warf",
            ["code"] = Concept(RxNorm, "11289", null),
        };
        var active = new JsonObject
        {
            ["resourceType"] = "MedicationRequest",
            ["id"] = "active-warf",
            ["status"] = "active",
            ["intent"] = "order",
            ["medicationReference"] = new JsonObject { ["reference"] = "Medication/med-warf", ["display"] = "BRAND ANTICOAGULANT" },
        };
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign,
            DraftMedication(MedShape.BundleEntry, "draft-ibu", "ibuprofen", "5640"),
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet(active, medication) });

        var cards = await RunAsync(new DrugInteractionCdsHandler(), request);

        cards.Should().ContainSingle().Which.Summary.Should().Contain("BRAND ANTICOAGULANT");
    }

    [Fact]
    public async Task A_reference_that_resolves_to_nothing_and_has_no_display_gives_no_card_and_does_not_throw()
    {
        var order = new JsonObject
        {
            ["resourceType"] = "MedicationRequest",
            ["id"] = "draft-x",
            ["status"] = "draft",
            ["medicationReference"] = new JsonObject { ["reference"] = "Medication/not-sent" },
        };
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign, [order], new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(PenicillinAllergy()) });

        (await RunAsync(new DrugAllergyCdsHandler(), request)).Should().BeEmpty();
    }

    [Fact]
    public async Task Prior_auth_treats_an_stu3_procedure_request_like_a_service_request()
    {
        var procedure = new JsonObject
        {
            ["resourceType"] = "ProcedureRequest",
            ["id"] = "draft-mri",
            ["status"] = "draft",
            ["intent"] = "proposal",
            ["code"] = Concept("http://www.ama-assn.org/go/cpt", "70553", "MRI BRAIN W WO CONTRAST"),
        };
        var request = EpicOrderRequest(CdsHookNames.OrderSign, [procedure], prefetch: null);

        var cards = await RunAsync(new PriorAuthCdsHandler(), request);

        cards.Should().ContainSingle().Which.Summary.Should().Contain("MRI BRAIN");
    }

    [Fact]
    public async Task Drug_interaction_on_order_select_checks_only_the_newly_selected_order()
    {
        var drafts = DraftMedication(MedShape.BundleEntry, "draft-ibu", "ibuprofen", "5640")
            .Concat(DraftMedication(MedShape.BundleEntry, "draft-amox", "amoxicillin", "723"))
            .ToArray();
        var prefetch = new JsonObject { ["medications"] = CdsTestSupport.SearchSet(ActiveContainedMedication("active-warf", "11289", "WARFARIN 5 MG")) };
        var handler = new DrugInteractionCdsHandler();

        var ibuprofenSelected = await RunAsync(handler, EpicOrderRequest(CdsHookNames.OrderSelect, drafts, prefetch.DeepClone().AsObject(), selections: ["MedicationRequest/draft-ibu"]));
        var amoxicillinSelected = await RunAsync(handler, EpicOrderRequest(CdsHookNames.OrderSelect, CloneAll(drafts), prefetch.DeepClone().AsObject(), selections: ["MedicationRequest/draft-amox"]));

        ibuprofenSelected.Should().ContainSingle();
        amoxicillinSelected.Should().BeEmpty("the ibuprofen order was selected earlier; only new selections are checked");
    }

    [Fact]
    public async Task Renal_dose_on_order_select_checks_only_the_newly_selected_order()
    {
        var drafts = DraftMedication(MedShape.Contained, "draft-met", "metformin", "6809")
            .Concat(DraftMedication(MedShape.Contained, "draft-ibu", "ibuprofen", "5640"))
            .ToArray();
        var handler = new RenalDoseCdsHandler();

        var metforminSelected = await RunAsync(handler, EpicOrderRequest(
            CdsHookNames.OrderSelect, drafts, new JsonObject { ["egfr"] = CdsTestSupport.SearchSet(Egfr(25)) }, selections: ["MedicationRequest/draft-met"]));
        var ibuprofenSelected = await RunAsync(handler, EpicOrderRequest(
            CdsHookNames.OrderSelect, CloneAll(drafts), new JsonObject { ["egfr"] = CdsTestSupport.SearchSet(Egfr(25)) }, selections: ["MedicationRequest/draft-ibu"]));

        metforminSelected.Should().ContainSingle();
        ibuprofenSelected.Should().BeEmpty();
    }

    [Fact]
    public async Task Drug_allergy_on_order_select_with_epic_drafts_checks_the_selected_order()
    {
        var drafts = DraftMedication(MedShape.BundleEntry, "draft-ibu", "ibuprofen", "5640")
            .Concat(DraftMedication(MedShape.BundleEntry, "draft-amox", "amoxicillin", "723"))
            .ToArray();

        var cards = await RunAsync(new DrugAllergyCdsHandler(), EpicOrderRequest(
            CdsHookNames.OrderSelect, drafts, new JsonObject { ["allergies"] = CdsTestSupport.SearchSet(PenicillinAllergy()) },
            selections: ["MedicationRequest/draft-amox"]));

        cards.Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public void Every_sample_request_is_epic_shaped(string key, string hook)
    {
        var request = Handler(key).BuildSampleRequest(hook);

        request.Context!["userId"]!.GetValue<string>().Should().StartWith("PractitionerRole/");
        request.Extension![CdsEpicExtensions.RequestBpaTriggerAction].Should().NotBeNull();
        if (hook != CdsHookNames.PatientView)
        {
            var draftOrders = request.Context["draftOrders"]!.AsObject();
            draftOrders["resourceType"]!.GetValue<string>().Should().Be("Bundle");
            draftOrders["type"]!.GetValue<string>().Should().Be("collection");
            draftOrders["entry"]!.AsArray().Should().AllSatisfy(entry => entry!["fullUrl"].Should().NotBeNull());
        }

        if (hook == CdsHookNames.OrderSelect)
        {
            request.Context["selections"]!.AsArray().Should().NotBeEmpty();
        }

        var json = JsonSerializer.Serialize(request);
        json.Should().NotContain("medicationCodeableConcept", "Epic sends medicationReference since Feb 2024");
    }

    // ---- Topic codes ----

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public async Task Every_card_carries_a_static_alphanumeric_topic_code(string key, string hook)
    {
        var handler = Handler(key);

        var first = await RunAsync(handler, handler.BuildSampleRequest(hook));
        var secondRequest = handler.BuildSampleRequest(hook);
        secondRequest.Context!["patientId"] = "another-patient";
        var second = await RunAsync(handler, secondRequest);

        first.Should().NotBeEmpty();
        first.Should().AllSatisfy(card =>
        {
            card.Source.Topic.Should().NotBeNull();
            card.Source.Topic!.System.Should().Be(CdsTopic.System);
            card.Source.Topic.Code.Should().MatchRegex("^[A-Za-z0-9]{1,100}$");
        });
        second.Select(card => card.Source.Topic!.Code).Should().Equal(
            first.Select(card => card.Source.Topic!.Code), "a topic code is static per rule, never per patient or call");
    }

    [Fact]
    public async Task Topic_codes_differ_by_rule_and_survive_the_evaluator()
    {
        var careGap = new CareGapCdsHandler();
        var cards = await EvaluateAsync(Instance(careGap, CdsHookNames.PatientView), careGap, careGap.BuildSampleRequest(CdsHookNames.PatientView));

        cards.Select(card => card.Source.Topic!.Code).Should().BeEquivalentTo("CareGapHba1c", "CareGapInfluenza", "CareGapColorectal");
    }

    [Fact]
    public async Task Drug_interaction_topic_follows_the_severity()
    {
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign,
            DraftMedication(MedShape.BundleEntry, "draft-sil", "sildenafil", "136411")
                .Concat(DraftMedication(MedShape.BundleEntry, "draft-nitro", "nitroglycerin", "4917")).ToArray(),
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet() });

        var cards = await RunAsync(new DrugInteractionCdsHandler(), request);

        cards.Single().Source.Topic!.Code.Should().Be("DrugInteractionContraindicated");
    }

    // ---- Dismiss reasons and links per instance ----

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public async Task Cards_offer_the_instance_dismiss_reasons_else_the_defaults(string key, string hook)
    {
        var handler = Handler(key);
        var own = new[] { new CdsOverrideReasonOption("pt_declined", "Patient declined"), new CdsOverrideReasonOption("OUTSIDE-RX", "Prescribed elsewhere") };

        var withOwn = await EvaluateAsync(Instance(handler, hook, reasons: own), handler, handler.BuildSampleRequest(hook));
        var withDefaults = await EvaluateAsync(Instance(handler, hook), handler, handler.BuildSampleRequest(hook));

        withOwn.Should().NotBeEmpty().And.AllSatisfy(card =>
        {
            card.OverrideReasons!.Select(r => r.Code).Should().Equal("pt_declined", "OUTSIDE-RX");
            card.OverrideReasons.Should().AllSatisfy(r => r.System.Should().Be(CdsOverrideReasonOption.System));
            card.OverrideReasons!.Select(r => r.Display).Should().Equal("Patient declined", "Prescribed elsewhere");
        });
        withDefaults.Should().AllSatisfy(card =>
            card.OverrideReasons!.Select(r => r.Code).Should().Equal(CdsOverrideReasonOption.Defaults.Select(r => r.Code)));
    }

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public async Task Cards_carry_the_instance_links_with_smart_only_fields_on_smart_links(string key, string hook)
    {
        var handler = Handler(key);
        var links = new[]
        {
            new CdsCardLinkOption("Guideline", "https://guides.example.org/x", CdsCardLinkTypes.Absolute),
            new CdsCardLinkOption("Open app", "https://app.example.org/launch", CdsCardLinkTypes.Smart, "ctx-1", true),
        };

        var cards = await EvaluateAsync(Instance(handler, hook, links: links), handler, handler.BuildSampleRequest(hook));

        cards.Should().NotBeEmpty().And.AllSatisfy(card =>
        {
            var absolute = card.Links!.Single(l => l.Label == "Guideline");
            absolute.Type.Should().Be("absolute");
            absolute.AppContext.Should().BeNull();
            absolute.Autolaunchable.Should().BeNull();
            var smart = card.Links!.Single(l => l.Label == "Open app");
            smart.Type.Should().Be("smart");
            smart.AppContext.Should().Be("ctx-1");
            smart.Autolaunchable.Should().BeTrue();
        });

        var json = JsonSerializer.Serialize(cards[0]);
        json.Should().Contain("\"autolaunchable\":true").And.Contain("\"appContext\":\"ctx-1\"");
    }

    [Fact]
    public async Task Prior_auth_keeps_its_own_submission_link_before_the_instance_links()
    {
        var handler = new PriorAuthCdsHandler();
        var instance = Instance(handler, CdsHookNames.OrderSign,
            links: [new CdsCardLinkOption("Policy", "https://payer.example.org/policy", CdsCardLinkTypes.Absolute)],
            settingsJson: """{"submissionUrl":"https://payer.example.org/pa"}""");

        var cards = await EvaluateAsync(instance, handler, handler.BuildSampleRequest(CdsHookNames.OrderSign));

        cards.Single().Links!.Select(l => l.Label).Should().Equal("Start prior authorization", "Policy");
    }

    [Fact]
    public async Task Without_instance_links_cards_have_no_links_member()
    {
        var handler = new DrugAllergyCdsHandler();

        var cards = await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign), handler, handler.BuildSampleRequest(CdsHookNames.OrderSign));

        cards.Single().Links.Should().BeNull();
        JsonSerializer.Serialize(cards.Single()).Should().NotContain("\"links\"");
    }

    // ---- One-click fixes ----

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public async Task Fixes_are_off_by_default_so_no_card_carries_suggestions(string key, string hook)
    {
        var handler = Handler(key);

        var cards = await EvaluateAsync(Instance(handler, hook), handler, handler.BuildSampleRequest(hook));

        cards.Should().NotBeEmpty().And.AllSatisfy(card =>
        {
            card.Suggestions.Should().BeNull();
            card.SelectionBehavior.Should().BeNull();
        });
        JsonSerializer.Serialize(cards).Should().NotContain("suggestions").And.NotContain("selectionBehavior");
    }

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public async Task Fix_types_switched_on_under_a_master_switch_that_is_off_offer_nothing(string key, string hook)
    {
        var handler = Handler(key);
        var fixes = new CdsFixSettings(false, AllFixTypesOn(handler));

        var cards = await EvaluateAsync(Instance(handler, hook, fixes), handler, handler.BuildSampleRequest(hook));

        cards.Should().AllSatisfy(card => card.Suggestions.Should().BeNull());
    }

    [Theory]
    [MemberData(nameof(HandlerHooks))]
    public async Task Every_offered_fix_meets_the_epic_rules(string key, string hook)
    {
        var handler = Handler(key);
        var request = handler.BuildSampleRequest(hook);
        var draftIds = DraftOrderIds(request);

        var cards = await EvaluateAsync(Instance(handler, hook, new CdsFixSettings(true, AllFixTypesOn(handler))), handler, request);

        cards.Should().Contain(card => card.Suggestions != null, $"{key} offers at least one fix once all its fix types are on");
        foreach (var card in cards.Where(card => card.Suggestions is not null))
        {
            card.SelectionBehavior.Should().Be(CdsSelectionBehavior.Any);
            card.Suggestions!.Select(s => s.Uuid).Should().OnlyHaveUniqueItems();
            foreach (var suggestion in card.Suggestions!)
            {
                suggestion.Label.Should().NotBeNullOrWhiteSpace();
                Guid.TryParse(suggestion.Uuid, out _).Should().BeTrue();
                var action = suggestion.Actions.Should().ContainSingle("Epic supports one action per suggestion").Subject;
                CdsActionTypes.All.Should().Contain(action.Type);
                if (action.Type == CdsActionTypes.Delete)
                {
                    draftIds.Should().Contain(action.ResourceId!, "a delete names a draft order of this request");
                    action.Resource.Should().BeNull();
                }
                else
                {
                    var resource = action.Resource!;
                    var type = resource["resourceType"]!.GetValue<string>();
                    type.Should().BeOneOf("MedicationRequest", "ServiceRequest", "Condition");
                    resource["subject"]!["reference"]!.GetValue<string>().Should().Be($"Patient/{request.Context!["patientId"]!.GetValue<string>()}");
                    if (type != "Condition")
                    {
                        resource["status"]!.GetValue<string>().Should().Be("draft");
                        resource["intent"]!.GetValue<string>().Should().Be("proposal");
                    }
                }
            }
        }
    }

    [Fact]
    public async Task Drug_allergy_offers_remove_alternative_and_diagnosis_fixes_in_epic_shape()
    {
        var handler = new DrugAllergyCdsHandler();
        var fixes = new CdsFixSettings(
            true,
            [
                new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, []),
                new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
                    [new CdsFixCodeMapping("penicillin", CdsFixCodeSystems.RxNormOid, "18631", "azithromycin 250 MG")]),
                new CdsFixTypeSetting(CdsFixTypeKeys.AddProblem, true,
                    [new CdsFixCodeMapping(null, CdsFixCodeSystems.Icd10Cm, "Z88.0", "Allergy status to penicillin")]),
                new CdsFixTypeSetting(CdsFixTypeKeys.AddEncounterDiagnosis, true,
                    [new CdsFixCodeMapping(null, CdsFixCodeSystems.Icd10Cm, "Z88.0", "Allergy status to penicillin")]),
            ],
            CdsFixTypeKeys.RemoveDraftOrder);
        var request = handler.BuildSampleRequest(CdsHookNames.OrderSign);

        var card = (await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, request)).Single();

        var suggestions = card.Suggestions!;
        suggestions.Should().HaveCount(4);
        card.SelectionBehavior.Should().Be("any");

        var remove = suggestions[0].Actions.Single();
        remove.Type.Should().Be("delete");
        remove.ResourceId.Should().Be("MedicationRequest/draft-amoxicillin");
        suggestions[0].IsRecommended.Should().BeTrue();
        suggestions.Skip(1).Should().AllSatisfy(s => s.IsRecommended.Should().BeNull());

        var alternative = suggestions[1].Actions.Single().Resource!;
        alternative["resourceType"]!.GetValue<string>().Should().Be("MedicationRequest");
        alternative["status"]!.GetValue<string>().Should().Be("draft");
        alternative["medicationCodeableConcept"]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be("urn:oid:2.16.840.1.113883.6.88");
        alternative["medicationCodeableConcept"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("18631");

        var problem = suggestions[2].Actions.Single().Resource!;
        problem["resourceType"]!.GetValue<string>().Should().Be("Condition");
        problem["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("problem-list-item");
        problem["code"]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be("urn:oid:2.16.840.1.113883.6.90");
        problem["encounter"].Should().BeNull();

        var diagnosis = suggestions[3].Actions.Single().Resource!;
        diagnosis["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("encounter-diagnosis");
        diagnosis["encounter"]!["reference"]!.GetValue<string>().Should().Be("Encounter/sample-encounter-1");

        var json = JsonNode.Parse(JsonSerializer.Serialize(card))!;
        json["selectionBehavior"]!.GetValue<string>().Should().Be("any");
        json["suggestions"]![0]!["isRecommended"]!.GetValue<bool>().Should().BeTrue();
        json["suggestions"]![0]!["actions"]![0]!["resourceId"]!.GetValue<string>().Should().Be("MedicationRequest/draft-amoxicillin");
        json["suggestions"]![1]!["actions"]![0]!["type"]!.GetValue<string>().Should().Be("create");
    }

    [Fact]
    public async Task An_encounter_diagnosis_is_offered_only_inside_an_encounter()
    {
        var handler = new DrugAllergyCdsHandler();
        var fixes = new CdsFixSettings(true,
        [
            new CdsFixTypeSetting(CdsFixTypeKeys.AddEncounterDiagnosis, true,
                [new CdsFixCodeMapping(null, CdsFixCodeSystems.Icd10Cm, "Z88.0", "Allergy status to penicillin")]),
        ]);
        var request = handler.BuildSampleRequest(CdsHookNames.OrderSign);
        request.Context!.Remove("encounterId");

        var card = (await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, request)).Single();

        card.Suggestions.Should().BeNull();
        card.SelectionBehavior.Should().BeNull();
    }

    [Fact]
    public async Task A_fix_with_no_mapping_for_the_trigger_and_no_catch_all_is_not_offered()
    {
        var handler = new DrugAllergyCdsHandler();
        var fixes = new CdsFixSettings(true,
        [
            new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
                [new CdsFixCodeMapping("cephalosporin", CdsFixCodeSystems.EpicPreferenceListItem, "PL-1", "Some drug")]),
        ]);

        var card = (await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, handler.BuildSampleRequest(CdsHookNames.OrderSign))).Single();

        card.Suggestions.Should().BeNull();
    }

    [Fact]
    public async Task A_trigger_specific_mapping_wins_over_the_catch_all()
    {
        var handler = new DrugAllergyCdsHandler();
        var fixes = new CdsFixSettings(true,
        [
            new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
            [
                new CdsFixCodeMapping(null, CdsFixCodeSystems.EpicPreferenceListItem, "PL-ANY", "Generic alternative"),
                new CdsFixCodeMapping("penicillin", CdsFixCodeSystems.EpicPreferenceListItem, "PL-PEN", "Penicillin alternative"),
            ]),
        ]);

        var card = (await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, handler.BuildSampleRequest(CdsHookNames.OrderSign))).Single();

        var coding = card.Suggestions!.Single().Actions.Single().Resource!["medicationCodeableConcept"]!["coding"]![0]!;
        coding["system"]!.GetValue<string>().Should().Be(CdsFixCodeSystems.EpicPreferenceListItem);
        coding["code"]!.GetValue<string>().Should().Be("PL-PEN");
    }

    [Fact]
    public async Task Drug_interaction_between_two_drafts_offers_removing_either()
    {
        var handler = new DrugInteractionCdsHandler();
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign,
            DraftMedication(MedShape.BundleEntry, "draft-sil", "sildenafil", "136411")
                .Concat(DraftMedication(MedShape.BundleEntry, "draft-nitro", "nitroglycerin", "4917")).ToArray(),
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet() });
        var fixes = new CdsFixSettings(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, [])]);

        var card = (await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, request)).Single();

        card.Suggestions!.Select(s => s.Actions.Single().ResourceId)
            .Should().BeEquivalentTo("MedicationRequest/draft-sil", "MedicationRequest/draft-nitro");
    }

    [Fact]
    public async Task Drug_interaction_between_two_drafts_pre_selects_removing_only_one()
    {
        var handler = new DrugInteractionCdsHandler();
        var request = EpicOrderRequest(
            CdsHookNames.OrderSign,
            DraftMedication(MedShape.BundleEntry, "draft-sil", "sildenafil", "136411")
                .Concat(DraftMedication(MedShape.BundleEntry, "draft-nitro", "nitroglycerin", "4917")).ToArray(),
            new JsonObject { ["medications"] = CdsTestSupport.SearchSet() });
        var fixes = new CdsFixSettings(
            true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, [])], CdsFixTypeKeys.RemoveDraftOrder);

        var cards = await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, request);

        cards.Should().AllSatisfy(card => card.Suggestions!.Count(s => s.IsRecommended == true).Should().BeLessThanOrEqualTo(1));
        var recommendedDeletes = cards.SelectMany(card => card.Suggestions!)
            .Where(s => s.IsRecommended == true)
            .Select(s => s.Actions.Single().ResourceId)
            .ToArray();
        recommendedDeletes.Should().OnlyHaveUniqueItems().And.HaveCount(1, "Epic pre-selects every recommended fix");
    }

    [Fact]
    public async Task Drug_interaction_with_an_active_medication_never_offers_to_delete_it()
    {
        var handler = new DrugInteractionCdsHandler();
        var fixes = new CdsFixSettings(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, [])]);

        var card = (await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, handler.BuildSampleRequest(CdsHookNames.OrderSign))).Single();

        card.Suggestions!.Select(s => s.Actions.Single().ResourceId).Should().Equal("MedicationRequest/draft-ibuprofen");
    }

    [Fact]
    public async Task Renal_dose_offers_the_alternative_mapped_for_the_ingredient()
    {
        var handler = new RenalDoseCdsHandler();
        var fixes = new CdsFixSettings(true,
        [
            new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
                [new CdsFixCodeMapping("metformin", CdsFixCodeSystems.Ndc, "00000-1111-22", "sitagliptin 50 MG")]),
        ]);

        var card = (await EvaluateAsync(Instance(handler, CdsHookNames.OrderSign, fixes), handler, handler.BuildSampleRequest(CdsHookNames.OrderSign))).Single();

        var coding = card.Suggestions!.Single().Actions.Single().Resource!["medicationCodeableConcept"]!["coding"]![0]!;
        coding["system"]!.GetValue<string>().Should().Be("urn:oid:2.16.840.1.113883.6.69");
    }

    [Fact]
    public async Task Care_gap_offers_the_mapped_test_order_only_on_its_own_gap()
    {
        var handler = new CareGapCdsHandler();
        var fixes = new CdsFixSettings(true,
        [
            new CdsFixTypeSetting(CdsFixTypeKeys.AddTestOrder, true,
                [new CdsFixCodeMapping("hba1c", CdsFixCodeSystems.EpicPreferenceListItem, "PL-4548", "HEMOGLOBIN A1C")]),
            new CdsFixTypeSetting(CdsFixTypeKeys.AddProblem, true,
                [new CdsFixCodeMapping("hba1c", CdsFixCodeSystems.Icd10Cm, "E11.9", "Type 2 diabetes mellitus without complications")]),
        ], CdsFixTypeKeys.AddTestOrder);

        var cards = await EvaluateAsync(Instance(handler, CdsHookNames.PatientView, fixes), handler, handler.BuildSampleRequest(CdsHookNames.PatientView));

        var hba1c = cards.Single(card => card.Source.Topic!.Code == "CareGapHba1c");
        hba1c.Suggestions.Should().HaveCount(2);
        var order = hba1c.Suggestions![0].Actions.Single().Resource!;
        order["resourceType"]!.GetValue<string>().Should().Be("ServiceRequest");
        order["status"]!.GetValue<string>().Should().Be("draft");
        order["code"]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be("urn:com.epic.cdshooks.action.code.system.preference-list-item");
        order["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("PL-4548");
        hba1c.Suggestions[0].IsRecommended.Should().BeTrue();
        cards.Where(card => card != hba1c).Should().AllSatisfy(card => card.Suggestions.Should().BeNull());
    }

    // ---- Builders (synthetic data only) ----

    private static CdsFixTypeSetting[] AllFixTypesOn(ICdsServiceHandler handler) =>
        handler.SupportedFixTypes.Select(descriptor => new CdsFixTypeSetting(
                descriptor.Key,
                true,
                descriptor.RequiresMapping
                    ? [new CdsFixCodeMapping(null, descriptor.CodeSystems![0].Value, "CODE-1", "Mapped item")]
                    : []))
            .ToArray();

    private static HashSet<string> DraftOrderIds(CdsRequest request) =>
        (request.Context?["draftOrders"]?["entry"] as JsonArray ?? [])
            .Select(entry => entry?["resource"])
            .OfType<JsonObject>()
            .Where(resource => resource["resourceType"]!.GetValue<string>() != "Medication")
            .Select(resource => $"{resource["resourceType"]!.GetValue<string>()}/{resource["id"]!.GetValue<string>()}")
            .ToHashSet(StringComparer.Ordinal);

    private static JsonObject[] CloneAll(IEnumerable<JsonObject> resources) =>
        resources.Select(resource => resource.DeepClone().AsObject()).ToArray();

    /// <summary>Like Epic: a collection Bundle with a fullUrl per entry (orders and the Medications they reference).</summary>
    private static CdsRequest EpicOrderRequest(string hook, IReadOnlyList<JsonObject> draftResources, JsonObject? prefetch, string[]? selections = null)
    {
        var context = new JsonObject
        {
            ["userId"] = "PractitionerRole/epic-role-1",
            ["patientId"] = PatientId,
            ["encounterId"] = "epic-encounter-1",
            ["draftOrders"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(draftResources.Select(resource => (JsonNode?)new JsonObject
                {
                    ["fullUrl"] = $"{FhirBase}/{resource["resourceType"]!.GetValue<string>()}/{resource["id"]!.GetValue<string>()}",
                    ["resource"] = resource,
                }).ToArray()),
            },
        };
        if (selections is not null)
        {
            context["selections"] = new JsonArray(selections.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        return new CdsRequest
        {
            Hook = hook,
            HookInstance = Guid.NewGuid().ToString(),
            FhirServer = FhirBase,
            Context = context,
            Prefetch = prefetch,
            Extension = new JsonObject { [CdsEpicExtensions.RequestBpaTriggerAction] = "18" },
        };
    }

    /// <summary>
    /// A draft MedicationRequest (plus, for some shapes, its Medication as a separate draftOrders entry). Except in
    /// <see cref="MedShape.DisplayOnly"/>, the reference's display never names the ingredient, so a card proves the
    /// Medication's code was resolved.
    /// </summary>
    private static JsonObject[] DraftMedication(MedShape shape, string id, string ingredient, string rxNormCode)
    {
        var medicationId = $"med-{id}";
        var medication = new JsonObject
        {
            ["resourceType"] = "Medication",
            ["id"] = medicationId,
            ["code"] = Concept(shape == MedShape.OidCodeOnly ? "urn:oid:2.16.840.1.113883.6.88" : RxNorm, rxNormCode, null),
        };
        var order = new JsonObject
        {
            ["resourceType"] = "MedicationRequest",
            ["id"] = id,
            ["status"] = "draft",
            ["intent"] = "proposal",
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{PatientId}" },
        };

        JsonObject Reference(string reference, string? display)
        {
            var node = new JsonObject { ["reference"] = reference };
            if (display is not null)
            {
                node["display"] = display;
            }

            return node;
        }

        if (shape == MedShape.BundleEntry)
        {
            order["medicationReference"] = Reference($"Medication/{medicationId}", "BRAND PRODUCT 1 TAB");
            return [order, medication];
        }

        if (shape == MedShape.AbsoluteReference)
        {
            order["medicationReference"] = Reference($"{FhirBase}/Medication/{medicationId}", "BRAND PRODUCT 1 TAB");
            return [order, medication];
        }

        if (shape == MedShape.Contained)
        {
            order["contained"] = new JsonArray(medication);
            order["medicationReference"] = Reference($"#{medicationId}", "BRAND PRODUCT 1 TAB");
            return [order];
        }

        if (shape == MedShape.DisplayOnly)
        {
            order["medicationReference"] = Reference("Medication/not-sent", $"{ingredient.ToUpperInvariant()} 500 MG PO TABS");
            return [order];
        }

        order["medicationReference"] = Reference($"Medication/{medicationId}", null);
        return [order, medication];
    }

    private static JsonObject ActiveContainedMedication(string id, string rxNormCode, string display) => new()
    {
        ["resourceType"] = "MedicationRequest",
        ["id"] = id,
        ["status"] = "active",
        ["intent"] = "order",
        ["contained"] = new JsonArray(new JsonObject
        {
            ["resourceType"] = "Medication",
            ["id"] = $"med-{id}",
            ["code"] = Concept(RxNorm, rxNormCode, null),
        }),
        ["medicationReference"] = new JsonObject { ["reference"] = $"#med-{id}", ["display"] = display },
    };

    private static JsonObject PenicillinAllergy() => new()
    {
        ["resourceType"] = "AllergyIntolerance",
        ["clinicalStatus"] = Concept("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", "active", "Active"),
        ["criticality"] = "high",
        ["code"] = Concept(CdsSampleReferenceData.SnomedSystem, "91936005", "Allergy to penicillin"),
    };

    private static JsonObject Egfr(double value) => new()
    {
        ["resourceType"] = "Observation",
        ["status"] = "final",
        ["code"] = Concept(CdsSampleReferenceData.LoincSystem, "62238-1", "eGFR CKD-EPI"),
        ["effectiveDateTime"] = DateTime.UtcNow.Date.AddDays(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        ["valueQuantity"] = new JsonObject { ["value"] = value, ["unit"] = "mL/min/{1.73_m2}" },
    };

    private static JsonObject Concept(string system, string code, string? display)
    {
        var coding = new JsonObject { ["system"] = system, ["code"] = code };
        if (display is not null)
        {
            coding["display"] = display;
        }

        var concept = new JsonObject { ["coding"] = new JsonArray(coding) };
        if (display is not null)
        {
            concept["text"] = display;
        }

        return concept;
    }
}
