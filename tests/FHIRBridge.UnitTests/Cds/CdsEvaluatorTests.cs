using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Cds;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The evaluation path shared by POST /cds-services/{id} and the Test screen: request checks, the CDS Hooks 2.0
/// prefetch rule (412 vs. degrade), the time budget, and the card contract enforced whatever a handler returns.
/// </summary>
public sealed class CdsEvaluatorTests
{
    private const string PrefetchKey = "allergies";

    private static CdsEvaluator CreateEvaluator(int timeoutMs = 1500)
    {
        var settings = new Mock<ISystemSettingsCache>();
        settings.Setup(s => s.GetIntAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int fallback, CancellationToken _) => Task.FromResult(fallback));
        return new CdsEvaluator(settings.Object, CdsTestSupport.Options(new CdsOptions { TimeoutMs = timeoutMs }), NullLogger<CdsEvaluator>.Instance);
    }

    private static CdsServiceInstance Instance(string hook = CdsHookNames.OrderSign) =>
        new("test-service", "Test service", "fake", hook, "A test service.", true, null, "Segue Rules", "https://segue.example.org/rules", null);

    private static CdsRequest Request(JsonObject? prefetch = null, string hook = CdsHookNames.OrderSign) => new()
    {
        Hook = hook,
        HookInstance = Guid.NewGuid().ToString(),
        Context = new JsonObject { ["patientId"] = "p-1", ["userId"] = "Practitioner/u-1" },
        Prefetch = prefetch,
    };

    private static JsonObject WithPrefetch() => new() { [PrefetchKey] = CdsTestSupport.SearchSet() };

    private static Task<CdsEvaluation> EvaluateAsync(FakeCdsHandler handler, CdsRequest? request, int timeoutMs = 1500) =>
        CreateEvaluator(timeoutMs).EvaluateAsync(Instance(), handler, request, CancellationToken.None);

    // ---- Card contract ----

    [Fact]
    public async Task A_long_summary_is_cut_to_under_140_characters()
    {
        var handler = FakeCdsHandler.Returning(new CdsCard { Summary = new string('x', 300), Indicator = CdsIndicator.Warning });

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        var card = evaluation.Response!.Cards.Should().ContainSingle().Subject;
        card.Summary.Length.Should().Be(CdsCard.SummaryMaxLength).And.BeLessThan(140);
        card.Summary.Should().EndWith("…");
    }

    [Fact]
    public void Truncating_a_summary_never_splits_a_surrogate_pair()
    {
        // 137 ASCII characters, then an emoji (a surrogate pair) straddling the cut point.
        var summary = new string('x', CdsCard.SummaryMaxLength - 2) + "\U0001F48A" + new string('y', 20);

        var cut = CdsCard.TruncateSummary(summary);

        cut.Length.Should().BeLessThan(140);
        cut.Should().EndWith("…");
        char.IsHighSurrogate(cut[^2]).Should().BeFalse();
    }

    [Theory]
    [InlineData("hard-stop")]
    [InlineData("")]
    [InlineData("WARNING")]
    public async Task An_invalid_indicator_falls_back_to_info(string indicator)
    {
        var handler = FakeCdsHandler.Returning(new CdsCard { Summary = "Check this", Indicator = indicator });

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        evaluation.Response!.Cards.Single().Indicator.Should().Be(CdsIndicator.Info);
    }

    [Theory]
    [InlineData(CdsIndicator.Info)]
    [InlineData(CdsIndicator.Warning)]
    [InlineData(CdsIndicator.Critical)]
    public async Task A_valid_indicator_is_kept(string indicator)
    {
        var handler = FakeCdsHandler.Returning(new CdsCard { Summary = "Check this", Indicator = indicator });

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        evaluation.Response!.Cards.Single().Indicator.Should().Be(indicator);
    }

    [Fact]
    public async Task Cards_get_a_uuid_and_the_instance_source_and_blank_cards_are_dropped()
    {
        var handler = FakeCdsHandler.Returning(
            new CdsCard { Summary = "Real advice", Source = new CdsSource { Label = "spoofed", Url = "https://evil.example" } },
            new CdsCard { Summary = "   " });

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        var card = evaluation.Response!.Cards.Should().ContainSingle().Subject;
        Guid.TryParse(card.Uuid, out _).Should().BeTrue();
        card.Source.Label.Should().Be("Segue Rules");
        card.Source.Url.Should().Be("https://segue.example.org/rules");
        evaluation.Outcome.Should().Be(CdsCallOutcome.Success);
    }

    [Fact]
    public async Task At_most_25_cards_are_returned()
    {
        var handler = FakeCdsHandler.Returning(Enumerable.Range(0, 40).Select(i => new CdsCard { Summary = $"Card {i}" }).ToArray());

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        evaluation.Response!.Cards.Should().HaveCount(CdsEvaluator.MaxCards);
    }

    [Fact]
    public async Task No_cards_is_a_200_recorded_as_NoCards()
    {
        var evaluation = await EvaluateAsync(FakeCdsHandler.Returning(), Request(WithPrefetch()));

        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Outcome.Should().Be(CdsCallOutcome.NoCards);
        evaluation.Response!.Cards.Should().BeEmpty();
    }

    // ---- Request checks ----

    [Fact]
    public async Task A_missing_body_is_a_400()
    {
        var evaluation = await EvaluateAsync(FakeCdsHandler.Returning(), request: null);

        evaluation.Status.Should().Be(CdsCallStatus.BadRequest);
    }

    [Fact]
    public async Task A_hook_other_than_the_instance_hook_is_a_400()
    {
        var evaluation = await EvaluateAsync(FakeCdsHandler.Returning(), Request(WithPrefetch(), hook: CdsHookNames.PatientView));

        evaluation.Status.Should().Be(CdsCallStatus.BadRequest);
        evaluation.Detail.Should().Contain(CdsHookNames.OrderSign);
    }

    [Fact]
    public async Task A_missing_hook_instance_is_a_400()
    {
        var request = Request(WithPrefetch());
        request.HookInstance = " ";

        var evaluation = await EvaluateAsync(FakeCdsHandler.Returning(), request);

        evaluation.Status.Should().Be(CdsCallStatus.BadRequest);
    }

    [Fact]
    public async Task A_missing_context_is_a_400()
    {
        var request = Request(WithPrefetch());
        request.Context = null;

        var evaluation = await EvaluateAsync(FakeCdsHandler.Returning(), request);

        evaluation.Status.Should().Be(CdsCallStatus.BadRequest);
    }

    // ---- Prefetch rule (CDS Hooks 2.0) ----

    [Fact]
    public async Task Missing_prefetch_with_no_fhir_server_is_a_412_and_the_handler_does_not_run()
    {
        var handler = FakeCdsHandler.Returning(new CdsCard { Summary = "Should not appear" });

        var evaluation = await EvaluateAsync(handler, Request(prefetch: null));

        evaluation.Status.Should().Be(CdsCallStatus.PreconditionFailed);
        evaluation.Outcome.Should().Be(CdsCallOutcome.PreconditionFailed);
        evaluation.Detail.Should().Contain(PrefetchKey);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Missing_prefetch_with_an_open_fhir_server_and_no_authorization_degrades_to_no_cards()
    {
        // fhirAuthorization is optional in CDS Hooks 2.0 (an open server needs none), so only fhirServer decides.
        var request = Request(prefetch: null);
        request.FhirServer = "https://ehr.example.org/fhir";

        var evaluation = await EvaluateAsync(FakeCdsHandler.Returning(), request);

        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
    }

    [Fact]
    public async Task Missing_prefetch_with_a_fhir_server_and_authorization_degrades_to_no_cards()
    {
        var handler = FakeCdsHandler.Returning(new CdsCard { Summary = "Should not appear" });
        var request = Request(prefetch: new JsonObject());
        request.FhirServer = "https://ehr.example.org/fhir";
        request.FhirAuthorization = new CdsFhirAuthorization { AccessToken = "secret", Subject = "client" };

        var evaluation = await EvaluateAsync(handler, request);

        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Response!.Cards.Should().BeEmpty();
        evaluation.Detail.Should().Contain(PrefetchKey).And.NotContain("secret");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_prefetch_key_sent_as_null_degrades_to_no_cards()
    {
        var evaluation = await EvaluateAsync(FakeCdsHandler.Returning(), Request(new JsonObject { [PrefetchKey] = null }));

        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
    }

    [Fact]
    public async Task A_handler_without_prefetch_runs_on_the_context_alone()
    {
        var handler = FakeCdsHandler.Returning(new CdsCard { Summary = "Context only" });
        handler.Prefetch = new Dictionary<string, string>();

        var evaluation = await EvaluateAsync(handler, Request(prefetch: null));

        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Response!.Cards.Should().ContainSingle();
    }

    // ---- Handler failures ----

    [Fact]
    public async Task A_handler_that_throws_is_a_500_and_the_detail_names_only_the_exception_type()
    {
        var handler = new FakeCdsHandler((_, _) => throw new InvalidOperationException("patient p-1 is allergic"));

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        evaluation.Status.Should().Be(CdsCallStatus.Error);
        evaluation.Detail.Should().Contain(nameof(InvalidOperationException)).And.NotContain("p-1");
    }

    [Fact]
    public async Task A_handler_over_its_time_budget_answers_200_with_no_cards()
    {
        var handler = new FakeCdsHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new CdsHandlerResult([new CdsCard { Summary = "Too late" }]);
        });

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()), timeoutMs: 100);

        evaluation.Status.Should().Be(CdsCallStatus.Ok);
        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.Response!.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task A_degraded_reason_from_the_handler_is_recorded_with_its_cards()
    {
        var handler = new FakeCdsHandler((_, _) =>
            Task.FromResult(new CdsHandlerResult([new CdsCard { Summary = "Partial" }], "The allergy list was not provided.")));

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        evaluation.Outcome.Should().Be(CdsCallOutcome.Degraded);
        evaluation.CardCount.Should().Be(1);
        evaluation.Detail.Should().Be("The allergy list was not provided.");
    }

    [Fact]
    public async Task Unset_settings_reach_the_handler_as_their_defaults()
    {
        CdsSettingsValues? seen = null;
        var handler = new FakeCdsHandler((context, _) =>
        {
            seen = context.Settings;
            return Task.FromResult(CdsHandlerResult.Empty);
        });

        var evaluation = await EvaluateAsync(handler, Request(WithPrefetch()));

        seen!.GetNumber("threshold").Should().Be(42);
        evaluation.ResolvedSettings["threshold"]!.ToJsonString().Should().Be("42");
    }

    /// <summary>A scriptable handler type for the evaluator: one setting, one prefetch key.</summary>
    private sealed class FakeCdsHandler : ICdsServiceHandler
    {
        private readonly Func<CdsHandlerContext, CancellationToken, Task<CdsHandlerResult>> _evaluate;

        public FakeCdsHandler(Func<CdsHandlerContext, CancellationToken, Task<CdsHandlerResult>> evaluate)
        {
            _evaluate = evaluate;
        }

        public static FakeCdsHandler Returning(params CdsCard[] cards) =>
            new((_, _) => Task.FromResult(new CdsHandlerResult(cards)));

        public int Calls { get; private set; }

        public IReadOnlyDictionary<string, string> Prefetch { get; set; } =
            new Dictionary<string, string> { [PrefetchKey] = "AllergyIntolerance?patient={{context.patientId}}" };

        public string Key => "fake";

        public string DisplayName => "Fake";

        public string Description => "Fake handler.";

        public IReadOnlyList<string> SupportedHooks { get; } = [CdsHookNames.OrderSign];

        public IReadOnlyList<CdsSettingDescriptor> Settings { get; } =
            [new("threshold", "Threshold", CdsSettingTypes.Number, JsonValue.Create(42))];

        public IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook) => Prefetch;

        public IReadOnlyDictionary<string, string> GetFallbackQueries(string hook) => Prefetch;

        public IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes => [];

        public CdsRequest BuildSampleRequest(string hook) => new() { Hook = hook };

        public Task<CdsHandlerResult> EvaluateAsync(CdsHandlerContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return _evaluate(context, cancellationToken);
        }
    }
}
