using System.Diagnostics;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Cds;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The evaluator's FHIR callback (Phase 2a: missing prefetch fetched from the caller's own fhirServer, only for an
/// authenticated issuer that allows it, on a service with "Fetch missing data" on, within the time budget, never
/// throwing and never leaking the token) and its suggestion rules (Phase 2b: fixes only when switched on, one action,
/// deletes only for this request's draft orders, created orders are drafts).
/// </summary>
public sealed class CdsEvaluatorCallbackTests
{
    private const string FhirBase = "https://ehr.example.org/api/FHIR/R4";
    private const string AccessToken = "epic-access-token-SECRET";
    private const string PatientId = "patient-callback-7";

    private static CdsTrustedIssuer Issuer(bool allowCallback = true, string issuer = FhirBase, string? fhirBaseUrl = null) =>
        new("Epic", "Epic", issuer, "https://ehr.example.org/jwks.json", null, null, true,
            expectedSubject: "cds-client", fhirBaseUrl: fhirBaseUrl, allowCallback: allowCallback);

    private static CdsServiceInstance Instance(bool fetchMissingData = true, CdsFixSettings? fixes = null) =>
        new("scripted-service", "Scripted", "scripted", CdsHookNames.OrderSign, "Description", true, null, "Segue", null, null,
            fetchMissingData, fixes);

    private static CdsRequest Request(JsonObject? prefetch = null, string? fhirServer = FhirBase) => new()
    {
        Hook = CdsHookNames.OrderSign,
        HookInstance = Guid.NewGuid().ToString(),
        FhirServer = fhirServer,
        FhirAuthorization = new CdsFhirAuthorization { AccessToken = AccessToken, Subject = "cds-client", Patient = PatientId },
        Context = new JsonObject
        {
            ["patientId"] = PatientId,
            ["userId"] = "PractitionerRole/r-1",
            ["draftOrders"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(
                    new JsonObject { ["resource"] = new JsonObject { ["resourceType"] = "MedicationRequest", ["id"] = "m1", ["status"] = "draft" } },
                    new JsonObject { ["resource"] = new JsonObject { ["resourceType"] = "Medication", ["id"] = "med-1" } }),
            },
        },
        Prefetch = prefetch,
    };

    private static JsonObject AllergyBundle() => CdsTestSupport.SearchSet(new JsonObject { ["resourceType"] = "AllergyIntolerance", ["id"] = "a1" });

    // ---- Callback: when it runs ----

    [Fact]
    public async Task Missing_prefetch_is_fetched_from_the_trusted_fhir_server_before_the_handler_runs()
    {
        var callback = FakeCallbackClient.Answering(key => AllergyBundle());
        JsonNode? seenByHandler = null;
        var handler = ScriptedHandler.Seeing(context => seenByHandler = context.Request.Prefetch?["allergies"]?.DeepClone());

        var evaluation = await CdsTestSupport.Evaluator(callback).EvaluateAsync(Instance(), handler, Request(), Issuer(), CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Success);
        evaluation.CallbackResourceCount.Should().Be(1);
        evaluation.CallbackMs.Should().NotBeNull();
        seenByHandler!["resourceType"]!.GetValue<string>().Should().Be("Bundle");

        var sent = callback.Requests.Should().ContainSingle().Subject;
        sent.FhirServer.Should().Be(FhirBase);
        sent.ExpectedFhirBaseUrl.Should().Be(FhirBase);
        sent.Authorization!.AccessToken.Should().Be(AccessToken, "the request's own fhirAuthorization is the only credential used");
        sent.Queries.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<string, string>("allergies", $"AllergyIntolerance?patient={PatientId}"));
    }

    [Fact]
    public async Task Only_absent_keys_are_fetched_and_keys_the_client_sent_are_never_overwritten()
    {
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());
        var handler = ScriptedHandler.Seeing(_ => { });
        handler.Prefetch = new Dictionary<string, string>
        {
            ["allergies"] = "AllergyIntolerance?patient={{context.patientId}}",
            ["patient"] = "Patient/{{context.patientId}}",
        };
        var sentPatient = new JsonObject { ["resourceType"] = "Patient", ["id"] = "sent-by-ehr" };
        var request = Request(new JsonObject { ["patient"] = sentPatient });

        await CdsTestSupport.Evaluator(callback).EvaluateAsync(Instance(), handler, request, Issuer(), CancellationToken.None);

        callback.Requests.Single().Queries.Keys.Should().Equal("allergies");
        request.Prefetch!["patient"]!["id"]!.GetValue<string>().Should().Be("sent-by-ehr");
    }

    [Fact]
    public async Task The_admin_test_run_has_no_issuer_and_never_calls_back()
    {
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());
        var handler = ScriptedHandler.Seeing(_ => { });

        var evaluation = await CdsTestSupport.Evaluator(callback).EvaluateAsync(Instance(), handler, Request(), CancellationToken.None);

        callback.Requests.Should().BeEmpty();
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("authenticated");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_issuer_with_callbacks_off_is_never_called_back()
    {
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());

        var evaluation = await CdsTestSupport.Evaluator(callback)
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(allowCallback: false), CancellationToken.None);

        callback.Requests.Should().BeEmpty();
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("callbacks are off");
        evaluation.CallbackResourceCount.Should().BeNull();
    }

    [Fact]
    public async Task A_service_with_fetch_missing_data_off_is_never_called_back()
    {
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());

        var evaluation = await CdsTestSupport.Evaluator(callback)
            .EvaluateAsync(Instance(fetchMissingData: false), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(), CancellationToken.None);

        callback.Requests.Should().BeEmpty();
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("Fetch missing data is off");
    }

    [Theory]
    [InlineData("https://attacker.example.org/api/FHIR/R4")]
    [InlineData("https://ehr.example.org/api/FHIR/DSTU2")]
    [InlineData("http://ehr.example.org/api/FHIR/R4")]
    [InlineData("not a url")]
    public async Task A_fhir_server_that_is_not_the_issuer_fhir_base_is_never_called(string fhirServer)
    {
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());

        var evaluation = await CdsTestSupport.Evaluator(callback)
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(fhirServer: fhirServer), Issuer(), CancellationToken.None);

        callback.Requests.Should().BeEmpty();
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("not the trusted issuer's FHIR base URL").And.NotContain("attacker");
    }

    [Fact]
    public async Task The_issuer_fhir_base_url_overrides_the_iss_value()
    {
        const string fhirBase = "https://fhir.ehr.example.org/R4";
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());
        var issuer = Issuer(issuer: "https://ehr.example.org/oauth", fhirBaseUrl: fhirBase + "/");

        var evaluation = await CdsTestSupport.Evaluator(callback)
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(fhirServer: fhirBase), issuer, CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Success);
        callback.Requests.Single().ExpectedFhirBaseUrl.Should().Be(fhirBase);
    }

    [Fact]
    public async Task Missing_prefetch_without_a_fhir_server_is_still_412_and_nothing_is_fetched()
    {
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());

        var evaluation = await CdsTestSupport.Evaluator(callback)
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(fhirServer: null), Issuer(), CancellationToken.None);

        evaluation.Status.Should().Be(CdsCallStatus.PreconditionFailed);
        callback.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_prefetch_makes_no_callback()
    {
        var callback = FakeCallbackClient.Answering(_ => AllergyBundle());

        var evaluation = await CdsTestSupport.Evaluator(callback).EvaluateAsync(
            Instance(), ScriptedHandler.Seeing(_ => { }), Request(new JsonObject { ["allergies"] = AllergyBundle() }), Issuer(), CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Success);
        evaluation.CallbackResourceCount.Should().BeNull();
        callback.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CdsHandlerKeys.RenalDose, CdsHookNames.OrderSign)]
    [InlineData(CdsHandlerKeys.CareGap, CdsHookNames.PatientView)]
    [InlineData(CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSelect)]
    [InlineData(CdsHandlerKeys.DrugInteraction, CdsHookNames.OrderSign)]
    public async Task A_built_in_handler_gets_its_whole_prefetch_through_the_real_callback_client(string handlerKey, string hook)
    {
        ICdsServiceHandler handler = handlerKey switch
        {
            CdsHandlerKeys.RenalDose => new FHIRBridge.Infrastructure.Cds.Handlers.RenalDoseCdsHandler(),
            CdsHandlerKeys.CareGap => new FHIRBridge.Infrastructure.Cds.Handlers.CareGapCdsHandler(),
            CdsHandlerKeys.DrugAllergy => new FHIRBridge.Infrastructure.Cds.Handlers.DrugAllergyCdsHandler(),
            _ => new FHIRBridge.Infrastructure.Cds.Handlers.DrugInteractionCdsHandler(),
        };
        var request = handler.BuildSampleRequest(hook);
        var served = request.Prefetch!;
        var queryToKey = handler.GetFallbackQueries(hook)
            .ToDictionary(pair => CdsPrefetchTemplate.Resolve(pair.Value, request.Context)!, pair => pair.Key);
        var http = new CdsFakeHttpHandler(message =>
        {
            var relative = Uri.UnescapeDataString(message.RequestUri!.PathAndQuery["/api/FHIR/R4/".Length..]);
            var key = queryToKey.Single(pair => Uri.UnescapeDataString(pair.Key) == relative).Value;
            return CdsFakeHttpHandler.Json(served[key]!.DeepClone());
        });
        request.Prefetch = null;
        request.FhirServer = FhirBase;
        request.FhirAuthorization = new CdsFhirAuthorization { AccessToken = AccessToken };
        var instance = new CdsServiceInstance($"{handlerKey}-cb", "Callback", handlerKey, hook, "Description", true, null, "Segue", null, null);
        var client = new CdsFhirCallbackClient(new HttpClient(http), CdsTestSupport.Options());

        var evaluation = await CdsTestSupport.Evaluator(client).EvaluateAsync(instance, handler, request, Issuer(), CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Success, evaluation.Detail);
        evaluation.Response!.Cards.Should().NotBeEmpty();
        http.Seen.Should().HaveCount(queryToKey.Count).And.AllSatisfy(seen => seen.Authorization.Should().Be($"Bearer {AccessToken}"));
    }

    // ---- Callback: failures degrade, never throw, never leak ----

    [Fact]
    public async Task A_failed_fetch_degrades_with_the_short_reason_and_the_handler_does_not_run()
    {
        var callback = FakeCallbackClient.Returning(new CdsCallbackResult(
            new Dictionary<string, JsonNode?> { ["allergies"] = null }, 0, 12, "HTTP 401"));
        var handler = ScriptedHandler.Seeing(_ => { });

        var evaluation = await CdsTestSupport.Evaluator(callback).EvaluateAsync(Instance(), handler, Request(), Issuer(), CancellationToken.None);

        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("allergies").And.Contain("callback: HTTP 401");
        evaluation.CallbackResourceCount.Should().Be(0);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_callback_client_that_throws_degrades_and_logs_only_the_exception_type()
    {
        var logger = new CdsCapturingLogger<CdsEvaluator>();
        var callback = new FakeCallbackClient((_, _) =>
            throw new InvalidOperationException($"GET {FhirBase}/AllergyIntolerance?patient={PatientId} with {AccessToken}"));

        var evaluation = await CdsTestSupport.Evaluator(callback, logger: logger)
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(), CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("callback failed");
        logger.All.Should().Contain(nameof(InvalidOperationException))
            .And.NotContain(AccessToken)
            .And.NotContain(PatientId);
        string.Join(" ", evaluation.Messages).Should().NotContain(AccessToken).And.NotContain(PatientId);
    }

    [Fact]
    public async Task A_hanging_fhir_server_degrades_within_the_call_budget()
    {
        var callback = new FakeCallbackClient(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        var stopwatch = Stopwatch.StartNew();

        var evaluation = await CdsTestSupport.Evaluator(callback, new CdsOptions { TimeoutMs = 300 })
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(), CancellationToken.None);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("ran out of time");
    }

    [Fact]
    public async Task The_callback_timeout_defaults_to_900_ms_and_stays_inside_the_call_budget()
    {
        var defaults = FakeCallbackClient.Answering(_ => AllergyBundle());
        var tooLong = FakeCallbackClient.Answering(_ => AllergyBundle());

        await CdsTestSupport.Evaluator(defaults).EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(), CancellationToken.None);
        await CdsTestSupport.Evaluator(tooLong, settingOverrides: new Dictionary<string, int> { [CdsOptions.CallbackTimeoutMsKey] = 60_000 })
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(), CancellationToken.None);

        defaults.Requests.Single().Timeout.Should().Be(TimeSpan.FromMilliseconds(900));
        tooLong.Requests.Single().Timeout.Should().BeLessThan(TimeSpan.FromMilliseconds(new CdsOptions().TimeoutMs));
    }

    [Fact]
    public async Task The_token_never_appears_in_the_evaluation_or_the_logs()
    {
        var logger = new CdsCapturingLogger<CdsEvaluator>();
        var callback = FakeCallbackClient.Returning(new CdsCallbackResult(
            new Dictionary<string, JsonNode?> { ["allergies"] = null }, 0, 5, "timed out"));

        var evaluation = await CdsTestSupport.Evaluator(callback, logger: logger)
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(), CancellationToken.None);

        var everything = $"{evaluation.Detail} {string.Join(" ", evaluation.Messages)} {logger.All}";
        everything.Should().NotContain(AccessToken).And.NotContain(PatientId);
    }

    // ---- Suggestions: the rules every fix must meet before it leaves Segue ----

    private static readonly CdsFixSettings FixesOn = new(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, [])]);

    [Fact]
    public async Task At_most_one_suggestion_per_card_and_one_delete_per_draft_order_stay_recommended()
    {
        CdsSuggestion Recommended(CdsSuggestion suggestion)
        {
            suggestion.IsRecommended = true;
            return suggestion;
        }

        var first = new CdsCard
        {
            Summary = "First",
            Suggestions = [Recommended(Delete("MedicationRequest/m1")), Recommended(Create(new JsonObject { ["resourceType"] = "Condition" }))],
        };
        var second = new CdsCard { Summary = "Second", Suggestions = [Recommended(Delete("MedicationRequest/m1"))] };
        var handler = ScriptedHandler.Returning(first, second);

        var evaluation = await CdsTestSupport.Evaluator().EvaluateAsync(
            Instance(fixes: FixesOn), handler, Request(new JsonObject { ["allergies"] = AllergyBundle() }), CancellationToken.None);

        var cards = evaluation.Response!.Cards;
        cards[0].Suggestions!.Select(s => s.IsRecommended).Should().Equal(true, null);
        cards[1].Suggestions!.Single().IsRecommended.Should().BeNull("an earlier card already pre-selects removing that draft");
    }

    [Fact]
    public async Task A_callback_cut_off_by_the_budget_is_still_recorded()
    {
        var callback = new FakeCallbackClient(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });

        var evaluation = await CdsTestSupport.Evaluator(callback, new CdsOptions { TimeoutMs = 300 })
            .EvaluateAsync(Instance(), ScriptedHandler.Seeing(_ => { }), Request(), Issuer(), CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.CallbackMs.Should().NotBeNull();
        evaluation.CallbackResourceCount.Should().Be(0);
    }

    [Fact]
    public async Task A_callback_that_uses_the_whole_budget_is_blamed_and_the_handler_does_not_run()
    {
        // Ignores the token and answers after the budget has fired.
        var callback = new FakeCallbackClient(async (_, _) =>
        {
            await Task.Delay(600);
            return new CdsCallbackResult(new Dictionary<string, JsonNode?> { ["allergies"] = AllergyBundle() }, 1, 600, null);
        });
        var handler = ScriptedHandler.Seeing(_ => { });

        var evaluation = await CdsTestSupport.Evaluator(callback, new CdsOptions { TimeoutMs = 300 })
            .EvaluateAsync(Instance(), handler, Request(), Issuer(), CancellationToken.None);

        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Detail.Should().Contain("Fetching missing data used the whole").And.NotContain("rules did not finish");
        evaluation.CallbackResourceCount.Should().Be(1);
        evaluation.CallbackMs.Should().Be(600);
        handler.Calls.Should().Be(0);
    }

    private static CdsSuggestion Delete(string? resourceId) => new()
    {
        Label = "Remove",
        Uuid = Guid.NewGuid().ToString(),
        Actions = [new CdsAction { Type = CdsActionTypes.Delete, Description = "Remove", ResourceId = resourceId }],
    };

    private static CdsSuggestion Create(JsonObject resource) => new()
    {
        Label = "Add",
        Uuid = Guid.NewGuid().ToString(),
        Actions = [new CdsAction { Type = CdsActionTypes.Create, Description = "Add", Resource = resource }],
    };

    private static async Task<CdsCard> SanitizedAsync(CdsServiceInstance instance, CdsCard card, CdsCapturingLogger<CdsEvaluator>? logger = null)
    {
        var handler = ScriptedHandler.Returning(card);
        var evaluation = await CdsTestSupport.Evaluator(logger: logger).EvaluateAsync(
            instance, handler, Request(new JsonObject { ["allergies"] = AllergyBundle() }), CancellationToken.None);
        return evaluation.Response!.Cards.Single();
    }

    [Fact]
    public async Task With_fixes_off_every_suggestion_is_dropped_and_only_the_count_is_logged()
    {
        var logger = new CdsCapturingLogger<CdsEvaluator>();
        var card = new CdsCard
        {
            Summary = "Advice",
            Suggestions = [Delete("MedicationRequest/m1"), Create(new JsonObject { ["resourceType"] = "Condition" })],
            SelectionBehavior = "at-most-one",
        };

        var sanitized = await SanitizedAsync(Instance(), card, logger);

        sanitized.Suggestions.Should().BeNull();
        sanitized.SelectionBehavior.Should().BeNull();
        logger.All.Should().Contain("2 suggestion(s)").And.NotContain("MedicationRequest/m1");
    }

    [Fact]
    public async Task With_fixes_on_only_suggestions_meeting_the_epic_rules_are_kept()
    {
        var keptDelete = Delete("MedicationRequest/m1");
        var keptCondition = Create(new JsonObject { ["resourceType"] = "Condition" });
        var keptDraftOrder = Create(new JsonObject { ["resourceType"] = "ServiceRequest", ["status"] = "draft" });
        var card = new CdsCard
        {
            Summary = "Advice",
            Suggestions =
            [
                keptDelete,
                Delete("MedicationRequest/not-in-this-request"),
                Delete("Medication/med-1"),
                Delete(null),
                keptCondition,
                keptDraftOrder,
                Create(new JsonObject { ["resourceType"] = "MedicationRequest", ["status"] = "active" }),
                Create(new JsonObject { ["resourceType"] = "ProcedureRequest" }),
                new CdsSuggestion
                {
                    Label = "Two actions",
                    Uuid = Guid.NewGuid().ToString(),
                    Actions =
                    [
                        new CdsAction { Type = CdsActionTypes.Delete, ResourceId = "MedicationRequest/m1" },
                        new CdsAction { Type = CdsActionTypes.Create, Resource = new JsonObject { ["resourceType"] = "Condition" } },
                    ],
                },
                new CdsSuggestion
                {
                    Label = "Update",
                    Uuid = Guid.NewGuid().ToString(),
                    Actions = [new CdsAction { Type = "update", Resource = new JsonObject { ["resourceType"] = "Condition" } }],
                },
                new CdsSuggestion { Label = "No uuid", Actions = [new CdsAction { Type = CdsActionTypes.Delete, ResourceId = "MedicationRequest/m1" }] },
                new CdsSuggestion { Label = "No action", Uuid = Guid.NewGuid().ToString() },
            ],
        };

        var sanitized = await SanitizedAsync(Instance(fixes: FixesOn), card);

        sanitized.Suggestions.Should().Equal(keptDelete, keptCondition, keptDraftOrder);
        sanitized.SelectionBehavior.Should().Be(CdsSelectionBehavior.Any);
    }

    [Fact]
    public async Task At_most_ten_suggestions_are_kept_per_card()
    {
        var card = new CdsCard
        {
            Summary = "Advice",
            Suggestions = Enumerable.Range(0, 15).Select(_ => Create(new JsonObject { ["resourceType"] = "Condition" })).ToArray(),
        };

        var sanitized = await SanitizedAsync(Instance(fixes: FixesOn), card);

        sanitized.Suggestions.Should().HaveCount(CdsEvaluator.MaxSuggestionsPerCard);
    }

    [Fact]
    public async Task A_card_without_suggestions_never_carries_a_selection_behavior()
    {
        var sanitized = await SanitizedAsync(Instance(fixes: FixesOn), new CdsCard { Summary = "Advice", SelectionBehavior = "any" });

        sanitized.SelectionBehavior.Should().BeNull();
    }

    // ---- Test doubles ----

    /// <summary>A handler on hook order-sign with one prefetch key ("allergies") and scripted cards.</summary>
    private sealed class ScriptedHandler : ICdsServiceHandler
    {
        private readonly Func<CdsHandlerContext, CdsHandlerResult> _evaluate;

        private ScriptedHandler(Func<CdsHandlerContext, CdsHandlerResult> evaluate)
        {
            _evaluate = evaluate;
        }

        public static ScriptedHandler Seeing(Action<CdsHandlerContext> look) =>
            new(context =>
            {
                look(context);
                return new CdsHandlerResult([new CdsCard { Summary = "Advice" }]);
            });

        public static ScriptedHandler Returning(params CdsCard[] cards) => new(_ => new CdsHandlerResult(cards));

        public int Calls { get; private set; }

        public IReadOnlyDictionary<string, string> Prefetch { get; set; } =
            new Dictionary<string, string> { ["allergies"] = "AllergyIntolerance?patient={{context.patientId}}" };

        public string Key => "scripted";

        public string DisplayName => "Scripted";

        public string Description => "Scripted handler.";

        public IReadOnlyList<string> SupportedHooks { get; } = [CdsHookNames.OrderSign];

        public IReadOnlyList<CdsSettingDescriptor> Settings { get; } = [];

        public IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes { get; } = [];

        public IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => Prefetch;

        public IReadOnlyDictionary<string, string> GetFallbackQueries(string hook) => Prefetch;

        public CdsRequest BuildSampleRequest(string hook) => new() { Hook = hook };

        public Task<CdsHandlerResult> EvaluateAsync(CdsHandlerContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_evaluate(context));
        }
    }
}
