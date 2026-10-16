using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Cds;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Observability;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// POST /cds-services/{id} and /feedback behind the controller: status mapping, exactly one metadata-only call log
/// row per call, and a security event for every rejected caller.
/// </summary>
public sealed class CdsHooksServiceTests : IDisposable
{
    private const string Origin = "https://segue.example.org";
    private const string Issuer = "https://sandbox.cds-hooks.org";
    private const string PatientId = "patient-7c1f";

    private readonly InMemoryCdsServiceInstanceRepository _instances = new();
    private readonly InMemoryCdsTrustedIssuerRepository _issuers = new();
    private readonly InMemoryCdsCallLogRepository _callLog = new();
    private readonly Mock<IGovernanceLogger> _governance = new();
    private readonly List<CdsCallMetric> _metrics = [];
    private readonly MemoryCache _jwksCache = new(new MemoryCacheOptions());
    private readonly CdsTestSigningKey _key = CdsTestSigningKey.Rsa();
    private readonly CdsReplayGuard _replayGuard = new(
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));
    private readonly CdsHooksService _service;

    public CdsHooksServiceTests()
    {
        _service = CreateService(Origin);
    }

    /// <param name="publicBaseUrl">The configured Cds:PublicBaseUrl, or null for none.</param>
    /// <param name="callbackClient">The FHIR callback client the evaluator gets (none by default).</param>
    private CdsHooksService CreateService(string? publicBaseUrl, ICdsFhirCallbackClient? callbackClient = null, ILogger<CdsHooksService>? logger = null)
    {
        var settings = new Mock<ISystemSettingsCache>();
        settings.Setup(s => s.GetIntAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int fallback, CancellationToken _) => Task.FromResult(fallback));
        settings.Setup(s => s.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string fallback, CancellationToken _) => Task.FromResult(fallback));
        var metrics = new Mock<ICdsMetrics>();
        metrics.Setup(m => m.RecordCall(It.IsAny<CdsCallMetric>())).Callback<CdsCallMetric>(_metrics.Add);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CdsPublicBaseUrl.ConfigurationKey] = publicBaseUrl })
            .Build();

        var options = CdsTestSupport.Options();
        var jwks = new CdsJwksProvider(new Mock<IHttpClientFactory>().Object, _jwksCache, options, NullLogger<CdsJwksProvider>.Instance);
        var validator = new CdsJwtValidator(_issuers, jwks, _replayGuard, options);

        return new CdsHooksService(
            _instances,
            new CdsServiceHandlerRegistry([new DrugAllergyCdsHandler(), new PriorAuthCdsHandler()]),
            _callLog,
            new CdsPublicBaseUrlResolver(settings.Object, configuration),
            validator,
            new CdsEvaluator(settings.Object, options, NullLogger<CdsEvaluator>.Instance, callbackClient),
            _governance.Object,
            metrics.Object,
            logger ?? NullLogger<CdsHooksService>.Instance);
    }

    public void Dispose()
    {
        _key.Dispose();
        _jwksCache.Dispose();
    }

    private static string ServiceUrl(string serviceId) => $"{Origin}/cds-services/{serviceId}";

    private async Task<CdsTrustedIssuer> TrustAsync()
    {
        var trusted = new CdsTrustedIssuer("Sandbox", "Other", Issuer, null, _key.Jwks, null, true);
        await _issuers.AddAsync(trusted, CancellationToken.None);
        return trusted;
    }

    private async Task<CdsServiceInstance> AddInstanceAsync(
        string serviceId = "drug-allergy", bool enabled = true, IEnumerable<Guid>? allowedIssuerIds = null, CdsFixSettings? fixes = null)
    {
        var instance = new CdsServiceInstance(
            serviceId, "Drug allergy", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Checks allergies.", enabled,
            null, "Segue", null, allowedIssuerIds, fixes: fixes);
        await _instances.AddAsync(instance, CancellationToken.None);
        return instance;
    }

    private CdsCallerContext Caller(string serviceId, string? audience = null) =>
        new($"Bearer {_key.CreateToken(Issuer, audience ?? ServiceUrl(serviceId))}", Origin);

    /// <summary>The handler's own sample, with an identifiable patient id written into context and prefetch.</summary>
    private static CdsRequest SampleWithPatient()
    {
        var request = new DrugAllergyCdsHandler().BuildSampleRequest(CdsHookNames.OrderSign);
        request.Context!["patientId"] = PatientId;
        request.FhirAuthorization = new CdsFhirAuthorization { AccessToken = "ehr-access-token", Subject = "cds-client", Patient = PatientId };
        return request;
    }

    private async Task<IReadOnlyList<CdsCallLog>> CallLogAsync() =>
        (await _callLog.GetPagedAsync(new CdsCallLogFilter(), 1, 100, CancellationToken.None)).Items;

    [Fact]
    public async Task A_signed_call_returns_cards_and_writes_one_metadata_only_log_row()
    {
        var trusted = await TrustAsync();
        var instance = await AddInstanceAsync();
        var request = SampleWithPatient();

        var result = await _service.InvokeAsync("drug-allergy", request, Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
        result.Response!.Cards.Should().ContainSingle();

        var row = (await CallLogAsync()).Should().ContainSingle().Subject;
        row.Outcome.Should().Be(CdsCallOutcome.Success);
        row.HttpStatus.Should().Be(200);
        row.CardCount.Should().Be(1);
        row.ServiceInstanceId.Should().Be(instance.Id);
        row.TrustedIssuerId.Should().Be(trusted.Id);
        row.Issuer.Should().Be(Issuer);
        row.Hook.Should().Be(CdsHookNames.OrderSign);
        row.HookInstance.Should().Be(request.HookInstance);

        var stored = JsonSerializer.Serialize(row);
        stored.Should().NotContain(PatientId)
            .And.NotContain("ehr-access-token")
            .And.NotContain("penicillin", "no prefetch or card content is stored")
            .And.NotContain("amoxicillin");
        _metrics.Should().ContainSingle().Which.Outcome.Should().Be(nameof(CdsCallOutcome.Success));
    }

    [Fact]
    public async Task Missing_prefetch_without_a_fhir_server_is_412_and_logged_without_patient_data()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var request = SampleWithPatient();
        request.Prefetch = null;
        request.FhirAuthorization = null;

        var result = await _service.InvokeAsync("drug-allergy", request, Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.PreconditionFailed);
        var row = (await CallLogAsync()).Single();
        row.Outcome.Should().Be(CdsCallOutcome.PreconditionFailed);
        row.HttpStatus.Should().Be(412);
        row.Detail.Should().Contain("allergies").And.NotContain(PatientId);
    }

    [Fact]
    public async Task A_call_without_a_token_is_401_logged_and_raises_a_security_event()
    {
        await TrustAsync();
        await AddInstanceAsync();

        var result = await _service.InvokeAsync("drug-allergy", SampleWithPatient(), new CdsCallerContext(null, Origin), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Unauthorized);
        result.Response.Should().BeNull();
        (await CallLogAsync()).Single().Outcome.Should().Be(CdsCallOutcome.Unauthorized);
        _governance.Verify(g => g.LogSecurityEventAsync(
            It.Is<SecurityEventEntry>(e => e.EventType == "CdsCallRejected" && e.Details != null && !e.Details.Contains(PatientId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_token_for_another_service_url_is_401()
    {
        await TrustAsync();
        await AddInstanceAsync();

        var result = await _service.InvokeAsync(
            "drug-allergy", SampleWithPatient(), Caller("drug-allergy", ServiceUrl("other-service")), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Unauthorized);
    }

    [Fact]
    public async Task Without_a_configured_public_base_url_the_audience_is_never_taken_from_the_request()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var service = CreateService(publicBaseUrl: null);

        // The token's aud matches the request origin exactly: a Host header must not be able to make that pass.
        var result = await service.InvokeAsync("drug-allergy", SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Unauthorized);
        var row = (await CallLogAsync()).Single();
        row.Outcome.Should().Be(CdsCallOutcome.Unauthorized);
        row.Detail.Should().Contain(CdsPublicBaseUrl.ConfigurationKey);
    }

    [Fact]
    public async Task Without_a_configured_public_base_url_an_issuer_audience_override_still_works()
    {
        var trusted = new CdsTrustedIssuer("Sandbox", "Other", Issuer, null, _key.Jwks, "urn:segue:cds", true);
        await _issuers.AddAsync(trusted, CancellationToken.None);
        await AddInstanceAsync();
        var service = CreateService(publicBaseUrl: null);

        var result = await service.InvokeAsync(
            "drug-allergy", SampleWithPatient(), Caller("drug-allergy", "urn:segue:cds"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
    }

    [Fact]
    public async Task The_service_id_is_logged_lower_case_whatever_casing_the_caller_used()
    {
        await TrustAsync();
        await AddInstanceAsync();

        await _service.InvokeAsync("Drug-Allergy", SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);
        await _service.InvokeAsync(" No-Such-Service ", SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);

        (await CallLogAsync()).Select(r => r.ServiceId).Should().BeEquivalentTo("drug-allergy", "no-such-service");
    }

    [Fact]
    public async Task A_huge_or_multi_line_iss_cannot_overflow_or_forge_the_security_event()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var hostileIssuer = "https://evil.example.org/" + new string('a', 2000) + "\r\nFAKE ENTRY";

        await _service.InvokeAsync(
            "drug-allergy", SampleWithPatient(), new CdsCallerContext($"Bearer {_key.CreateToken(hostileIssuer, ServiceUrl("drug-allergy"))}", Origin),
            CancellationToken.None);

        _governance.Verify(g => g.LogSecurityEventAsync(
            It.Is<SecurityEventEntry>(e => e.Details != null && e.Details.Length <= 1000 && !e.Details.Contains('\n') && !e.Details.Contains('\r')),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_client_that_hangs_up_still_leaves_one_call_log_row()
    {
        await TrustAsync();
        await AddInstanceAsync();
        using var hungUp = new CancellationTokenSource();
        await hungUp.CancelAsync();

        var act = () => _service.InvokeAsync("drug-allergy", SampleWithPatient(), Caller("drug-allergy"), hungUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var row = (await CallLogAsync()).Should().ContainSingle().Subject;
        row.HttpStatus.Should().Be(499);
        row.Outcome.Should().Be(CdsCallOutcome.Error);
        _metrics.Should().ContainSingle();
    }

    [Fact]
    public async Task A_call_refused_by_the_license_gate_still_leaves_its_row()
    {
        await _service.RecordUnlicensedAsync("Drug-Allergy", feedback: true);

        var row = (await CallLogAsync()).Single();
        row.Kind.Should().Be(CdsCallKind.Feedback);
        row.ServiceId.Should().Be("drug-allergy");
        row.HttpStatus.Should().Be(503);
        row.Detail.Should().Contain("license");
    }

    [Fact]
    public async Task An_issuer_off_the_service_allow_list_is_403()
    {
        await TrustAsync();
        await AddInstanceAsync(allowedIssuerIds: [Guid.NewGuid()]);

        var result = await _service.InvokeAsync("drug-allergy", SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Forbidden);
        (await CallLogAsync()).Single().Outcome.Should().Be(CdsCallOutcome.Forbidden);
    }

    [Theory]
    [InlineData("no-such-service")]
    [InlineData("../../etc/passwd")]
    public async Task An_unknown_service_is_404_tagged_unknown(string serviceId)
    {
        await TrustAsync();

        var result = await _service.InvokeAsync(serviceId, SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.NotFound);
        _metrics.Single().ServiceId.Should().Be("unknown");
    }

    [Fact]
    public async Task A_disabled_service_is_404()
    {
        await TrustAsync();
        await AddInstanceAsync(enabled: false);

        var result = await _service.InvokeAsync("drug-allergy", SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.NotFound);
    }

    [Fact]
    public async Task Discovery_lists_only_enabled_instances_with_their_prefetch()
    {
        await AddInstanceAsync("drug-allergy");
        await AddInstanceAsync("drug-allergy-off", enabled: false);

        var discovery = await _service.GetDiscoveryAsync(CancellationToken.None);

        var service = discovery.Services.Should().ContainSingle().Subject;
        service.Id.Should().Be("drug-allergy");
        service.Hook.Should().Be(CdsHookNames.OrderSign);
        service.Prefetch.Should().ContainKey("allergies");
    }

    [Fact]
    public async Task Feedback_is_accepted_and_logged_as_counts_without_the_user_comment()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var feedback = new CdsFeedbackRequest
        {
            Feedback =
            [
                new CdsFeedbackItem { Card = Guid.NewGuid().ToString(), Outcome = "accepted", OutcomeTimestamp = "2026-10-12T10:00:00Z" },
                new CdsFeedbackItem
                {
                    Card = Guid.NewGuid().ToString(),
                    Outcome = "overridden",
                    OutcomeTimestamp = "2026-10-12T10:00:00Z",
                    OverrideReason = new CdsOverrideReason
                    {
                        Reason = new CdsCoding { Code = "patient-tolerated" },
                        UserComment = $"Patient {PatientId} took it last year",
                    },
                },
            ],
        };

        var result = await _service.SubmitFeedbackAsync(
            "drug-allergy", feedback, Caller("drug-allergy", ServiceUrl("drug-allergy") + "/feedback"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
        var row = (await CallLogAsync()).Single();
        row.Kind.Should().Be(CdsCallKind.Feedback);
        row.CardCount.Should().Be(2);
        row.Detail.Should().Contain("Accepted: 1, overridden: 1").And.Contain("patient-tolerated").And.NotContain(PatientId);
    }

    [Fact]
    public async Task Feedback_with_an_invalid_outcome_is_400()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var feedback = new CdsFeedbackRequest { Feedback = [new CdsFeedbackItem { Card = "c", Outcome = "ignored" }] };

        var result = await _service.SubmitFeedbackAsync("drug-allergy", feedback, Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.BadRequest);
    }

    [Fact]
    public async Task An_unparseable_body_after_authentication_is_400()
    {
        await TrustAsync();
        await AddInstanceAsync();

        var result = await _service.InvokeAsync("drug-allergy", new CdsRequest(), Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.BadRequest);
        (await CallLogAsync()).Single().HttpStatus.Should().Be(400);
    }

    // ---- Epic (Phase 1b / 2): request extensions, fixes, feedback shapes, callback ----

    [Fact]
    public async Task Epic_request_extensions_are_recorded_on_the_call_log_row()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var request = SampleWithPatient();
        request.Extension![CdsEpicExtensions.RequestBpaTriggerAction] = 60;
        request.Extension[CdsEpicExtensions.RequestCriteriaId] = "BPA-1234";
        request.Extension[CdsEpicExtensions.RequestEpicVersion] = "11.2";
        request.Extension[CdsEpicExtensions.RequestImplementationVersion] = "2";

        await _service.InvokeAsync("drug-allergy", request, Caller("drug-allergy"), CancellationToken.None);

        var row = (await CallLogAsync()).Single();
        row.TriggerAction.Should().Be("60");
        row.CriteriaId.Should().Be("BPA-1234");
        row.EpicVersion.Should().Be("11.2");
        row.ImplementationVersion.Should().Be("2");
        row.CallbackResourceCount.Should().BeNull("every prefetch key was sent, so no callback ran");
    }

    [Fact]
    public async Task An_extension_value_that_is_not_a_short_token_is_dropped_rather_than_stored()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var request = SampleWithPatient();
        request.Extension![CdsEpicExtensions.RequestCriteriaId] = $"patient {PatientId} <script>";

        await _service.InvokeAsync("drug-allergy", request, Caller("drug-allergy"), CancellationToken.None);

        var row = (await CallLogAsync()).Single();
        row.CriteriaId.Should().BeNull();
        JsonSerializer.Serialize(row).Should().NotContain(PatientId);
    }

    [Fact]
    public async Task Fixes_offered_are_counted_on_the_call_log_row()
    {
        await TrustAsync();
        await AddInstanceAsync(fixes: new CdsFixSettings(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, [])]));

        var result = await _service.InvokeAsync("drug-allergy", SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);

        result.Response!.Cards.Single().Suggestions.Should().ContainSingle();
        (await CallLogAsync()).Single().SuggestionsOffered.Should().Be(1);
    }

    [Fact]
    public async Task Without_fixes_no_fix_is_offered_or_counted()
    {
        await TrustAsync();
        await AddInstanceAsync();

        var result = await _service.InvokeAsync("drug-allergy", SampleWithPatient(), Caller("drug-allergy"), CancellationToken.None);

        result.Response!.Cards.Single().Suggestions.Should().BeNull();
        (await CallLogAsync()).Single().SuggestionsOffered.Should().Be(0);
    }

    private const string EpicBatchedFeedback = """
        {"feedback":[
          {"card":"11111111-1111-1111-1111-111111111111","outcome":"overridden","outcomeTimestamp":"2026-10-12T10:00:00Z",
           "overrideReasons":{"extension":{
               "com.epic.cdshooks.feedback.overrideReason.reasonCategory":"45",
               "com.epic.cdshooks.feedback.overrideReason.reasonDisplay":"Patient Jane Roe declined",
               "com.epic.cdshooks.feedback.overrideReason.reasonTitle":"Declined by Jane Roe"},
             "userComment":"Jane Roe says she took it in 2019"}},
          {"card":"22222222-2222-2222-2222-222222222222","outcome":"overridden","outcomeTimestamp":"2026-10-12T10:00:01Z",
           "overrideReasons":{"reason":{"code":"will-monitor","display":"Will monitor Jane Roe","system":"urn:segue:cds:override-reason"},
             "userComment":"monitor Jane Roe weekly"}},
          {"card":"33333333-3333-3333-3333-333333333333","outcome":"overridden","outcomeTimestamp":"2026-10-12T10:00:02Z",
           "overrideReason":{"reason":{"code":"not-applicable","system":"urn:segue:cds:override-reason"},"userComment":"Jane Roe again"}},
          {"card":"44444444-4444-4444-4444-444444444444","outcome":"accepted","outcomeTimestamp":"2026-10-12T10:00:03Z",
           "acceptedSuggestions":[{"id":"aaaaaaaa-0000-0000-0000-000000000001"},{"id":"aaaaaaaa-0000-0000-0000-000000000002"}]},
          {"card":"55555555-5555-5555-5555-555555555555","outcome":"accepted","outcomeTimestamp":"2026-10-12T10:00:04Z",
           "acceptedSuggestions":[{"id":"aaaaaaaa-0000-0000-0000-000000000003"},{"id":""}]},
          {"card":"66666666-6666-6666-6666-666666666666","outcome":"overridden","outcomeTimestamp":"2026-10-12T10:00:05Z",
           "overrideReasons":{"reason":{"code":"will-monitor"}}}
        ]}
        """;

    [Fact]
    public async Task Epic_batched_feedback_records_counts_codes_and_accepted_fixes_but_never_comments_or_display_text()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var feedback = JsonSerializer.Deserialize<CdsFeedbackRequest>(EpicBatchedFeedback)!;

        var result = await _service.SubmitFeedbackAsync(
            "drug-allergy", feedback, Caller("drug-allergy", ServiceUrl("drug-allergy") + "/feedback"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
        var row = (await CallLogAsync()).Single();
        row.Kind.Should().Be(CdsCallKind.Feedback);
        row.CardCount.Should().Be(6, "each batched item is counted");
        row.SuggestionsAccepted.Should().Be(3, "blank acceptedSuggestions ids are not counted");
        row.SuggestionsOffered.Should().Be(0);
        row.OverrideReasonCodes.Should().Be(
            "45,will-monitor:2,not-applicable",
            "plural, singular and Epic's category extension, with a count per code across the batch");
        row.Detail.Should().Contain("Accepted: 2, overridden: 4").And.Contain("fixes accepted: 3");

        var stored = JsonSerializer.Serialize(row);
        stored.Should().NotContain("Jane Roe").And.NotContain("2019").And.NotContain("weekly").And.NotContain("Declined");
    }

    [Fact]
    public async Task A_reason_code_that_is_not_a_short_token_is_not_recorded()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var feedback = new CdsFeedbackRequest
        {
            Feedback =
            [
                new CdsFeedbackItem
                {
                    Card = Guid.NewGuid().ToString(),
                    Outcome = "overridden",
                    OverrideReasons = new CdsOverrideReason { Reason = new CdsCoding { Code = $"patient {PatientId} refused" } },
                },
            ],
        };

        await _service.SubmitFeedbackAsync("drug-allergy", feedback, Caller("drug-allergy"), CancellationToken.None);

        var row = (await CallLogAsync()).Single();
        row.OverrideReasonCodes.Should().BeNull();
        JsonSerializer.Serialize(row).Should().NotContain(PatientId);
    }

    [Fact]
    public void An_unusable_reason_code_falls_back_to_epics_reason_category()
    {
        var reason = new CdsOverrideReason
        {
            Reason = new CdsCoding { Code = "not a token" },
            Extension = new System.Text.Json.Nodes.JsonObject
            {
                [CdsEpicExtensions.FeedbackReasonCategory] = "45",
            },
        };

        reason.RecordableCode().Should().Be("45");
        new CdsOverrideReason { Reason = new CdsCoding { Code = "will-monitor" } }.RecordableCode().Should().Be("will-monitor");
    }

    [Fact]
    public void Reason_counts_round_trip_through_the_stored_form()
    {
        var stored = CdsReasonCodeCounts.Format(["will-monitor", "45", "will-monitor", "will-monitor"], 300);

        stored.Should().Be("will-monitor:3,45");
        CdsReasonCodeCounts.Parse(stored).Should().Equal(new CdsReasonCountDto("will-monitor", 3), new CdsReasonCountDto("45", 1));
        CdsReasonCodeCounts.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public async Task Recorded_reason_codes_are_cut_at_whole_codes_to_fit_the_column()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var feedback = new CdsFeedbackRequest
        {
            Feedback = Enumerable.Range(0, 20).Select(i => new CdsFeedbackItem
            {
                Card = Guid.NewGuid().ToString(),
                Outcome = "overridden",
                OverrideReason = new CdsOverrideReason { Reason = new CdsCoding { Code = $"reason-{i:00}-{new string('x', 20)}" } },
            }).ToArray(),
        };

        await _service.SubmitFeedbackAsync("drug-allergy", feedback, Caller("drug-allergy"), CancellationToken.None);

        var codes = (await CallLogAsync()).Single().OverrideReasonCodes!;
        codes.Length.Should().BeLessThanOrEqualTo(CdsCallLog.OverrideReasonCodesMaxLength);
        codes.Split(',').Should().AllSatisfy(code => code.Should().MatchRegex("^reason-\\d\\d-x{20}$"));
    }

    [Fact]
    public async Task Missing_prefetch_is_fetched_through_the_callback_and_recorded_without_the_token()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var allergies = new DrugAllergyCdsHandler().BuildSampleRequest(CdsHookNames.OrderSign).Prefetch!["allergies"]!;
        var callback = FakeCallbackClient.Answering(_ => allergies.DeepClone());
        var logger = new CdsCapturingLogger<CdsHooksService>();
        var service = CreateService(Origin, callback, logger);
        var request = SampleWithPatient();
        request.Prefetch = null;
        request.FhirServer = Issuer;

        var result = await service.InvokeAsync("drug-allergy", request, Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
        result.Response!.Cards.Should().ContainSingle();
        callback.Requests.Single().Authorization!.AccessToken.Should().Be("ehr-access-token");
        var row = (await CallLogAsync()).Single();
        row.Outcome.Should().Be(CdsCallOutcome.Success);
        row.CallbackResourceCount.Should().Be(1);
        row.CallbackMs.Should().NotBeNull();
        JsonSerializer.Serialize(row).Should().NotContain("ehr-access-token").And.NotContain(PatientId);
        logger.All.Should().NotContain("ehr-access-token").And.NotContain(PatientId);
    }

    [Fact]
    public async Task A_failed_callback_is_degraded_and_recorded_with_its_short_reason()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var callback = FakeCallbackClient.Returning(new CdsCallbackResult(
            new Dictionary<string, JsonNode?> { ["allergies"] = null }, 0, 40, "timed out"));
        var service = CreateService(Origin, callback);
        var request = SampleWithPatient();
        request.Prefetch = null;
        request.FhirServer = Issuer;

        var result = await service.InvokeAsync("drug-allergy", request, Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
        result.Response!.Cards.Should().BeEmpty();
        var row = (await CallLogAsync()).Single();
        row.Outcome.Should().Be(CdsCallOutcome.Degraded);
        row.Detail.Should().Contain("timed out").And.NotContain("ehr-access-token");
        row.CallbackResourceCount.Should().Be(0);
    }

    [Fact]
    public async Task A_rejected_caller_never_triggers_a_callback()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var callback = FakeCallbackClient.Answering(_ => CdsTestSupport.SearchSet());
        var service = CreateService(Origin, callback);
        var request = SampleWithPatient();
        request.Prefetch = null;
        request.FhirServer = Issuer;

        var result = await service.InvokeAsync("drug-allergy", request, Caller("drug-allergy", ServiceUrl("another")), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Unauthorized);
        callback.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Prefetch_content_never_reaches_the_call_log_even_when_the_handler_degrades()
    {
        await TrustAsync();
        await AddInstanceAsync();
        var request = SampleWithPatient();
        request.Prefetch = new JsonObject { ["allergies"] = null };

        var result = await _service.InvokeAsync("drug-allergy", request, Caller("drug-allergy"), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
        var row = (await CallLogAsync()).Single();
        row.Outcome.Should().Be(CdsCallOutcome.Degraded);
        JsonSerializer.Serialize(row).Should().NotContain(PatientId);
    }
}
