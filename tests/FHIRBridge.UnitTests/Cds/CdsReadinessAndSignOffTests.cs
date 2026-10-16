using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Cds;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// Phase 3 (Epic go-live readiness): the clinical sign-off on a service (recorded, kept across saves that do not
/// change what clinicians are told, cleared by saves that do) and the readiness rules.
/// </summary>
public sealed class CdsReadinessAndSignOffTests
{
    private const string EpicIss = "https://ehr.example.org/api/FHIR/R4";
    private const string EpicJwks = "https://ehr.example.org/api/epic/2019/Security/Open/PublicKeys/1/OAuth2CertsJWKS";
    private static readonly DateTime Now = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryCdsServiceInstanceRepository _instances = new();
    private readonly InMemoryCdsTrustedIssuerRepository _issuers = new();
    private readonly CdsServiceHandlerRegistry _registry =
        new([new DrugAllergyCdsHandler(), new DrugInteractionCdsHandler(), new RenalDoseCdsHandler(), new CareGapCdsHandler(), new PriorAuthCdsHandler()]);

    // ---- Domain: sign-off and when it is cleared ----

    [Fact]
    public void Sign_off_records_who_when_and_a_trimmed_note()
    {
        var instance = Instance();

        instance.SignOff("  Dr A. Patel, CMIO ", "  P&T committee  ", Now);

        instance.IsClinicallySignedOff.Should().BeTrue();
        instance.SignedOffBy.Should().Be("Dr A. Patel, CMIO");
        instance.SignOffNote.Should().Be("P&T committee");
        instance.SignedOffAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Sign_off_needs_a_name_and_a_short_note()
    {
        var instance = Instance();

        instance.Invoking(i => i.SignOff("  ", null, Now)).Should().Throw<BusinessRuleException>().WithMessage("*Reviewer name*");
        instance.Invoking(i => i.SignOff(new string('a', CdsServiceInstance.SignedOffByMaxLength + 1), null, Now))
            .Should().Throw<BusinessRuleException>();
        instance.Invoking(i => i.SignOff("Dr A", new string('n', CdsServiceInstance.SignOffNoteMaxLength + 1), Now))
            .Should().Throw<BusinessRuleException>().WithMessage("*note*");
        instance.IsClinicallySignedOff.Should().BeFalse();
    }

    [Fact]
    public void A_save_that_changes_nothing_clinicians_see_keeps_the_sign_off()
    {
        var issuerId = Guid.NewGuid();
        var instance = new CdsServiceInstance(
            "epic-drug-allergy", "Drug allergy", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Checks allergies.", true,
            """{"allergyIndicator":"warning","checkCrossReactivity":true}""", "Segue", null, null,
            fetchMissingData: true, fixes: Fixes(), overrideReasons: Reasons(), links: Links());
        instance.SignOff("Dr A", null, Now);

        // New name, id, description, source, off, issuer allow-list, fetch-missing-data; settings resent in another order.
        instance.Update(
            "renamed-allergy", "Renamed", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Other text", false,
            """{ "checkCrossReactivity": true, "allergyIndicator": "warning" }""", "Other source", "https://example.org/about",
            [issuerId], fetchMissingData: false, fixes: Fixes(), overrideReasons: Reasons(), links: Links());

        instance.IsClinicallySignedOff.Should().BeTrue();
        instance.SignedOffBy.Should().Be("Dr A");
    }

    public static TheoryData<string> ReviewedChanges() =>
    [
        "settings",
        "hook",
        "handler",
        "fixes",
        "reasons",
        "links",
    ];

    [Theory]
    [MemberData(nameof(ReviewedChanges))]
    public void A_save_that_changes_reviewed_content_clears_the_sign_off(string change)
    {
        var instance = Instance(settingsJson: """{"allergyIndicator":"warning"}""");
        instance.SignOff("Dr A", "Reviewed", Now);

        instance.Update(
            instance.ServiceId,
            instance.Title,
            change == "handler" ? CdsHandlerKeys.DrugInteraction : CdsHandlerKeys.DrugAllergy,
            change == "hook" ? CdsHookNames.OrderSelect : CdsHookNames.OrderSign,
            instance.Description,
            instance.IsEnabled,
            change == "settings" ? """{"allergyIndicator":"critical"}""" : """{"allergyIndicator":"warning"}""",
            instance.CardSourceLabel,
            instance.CardSourceUrl,
            instance.AllowedIssuerIds,
            instance.FetchMissingData,
            change == "fixes" ? new CdsFixSettings(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, false, [])], null) : Fixes(),
            change == "reasons" ? [new CdsOverrideReasonOption("other", "Other reason")] : Reasons(),
            change == "links" ? [new CdsCardLinkOption("Other", "https://guides.example.org/other", CdsCardLinkTypes.Absolute)] : Links());

        instance.IsClinicallySignedOff.Should().BeFalse();
        instance.SignedOffBy.Should().BeNull();
        instance.SignedOffAtUtc.Should().BeNull();
        instance.SignOffNote.Should().BeNull();
    }

    // ---- Admin service and setup sheet ----

    [Fact]
    public async Task The_admin_service_signs_off_clears_and_a_settings_change_clears_it_too()
    {
        var service = InstanceService();
        var created = await service.CreateAsync(SaveRequest(), CancellationToken.None);
        created.IsClinicallySignedOff.Should().BeFalse();
        created.UsesSampleReferenceData.Should().BeTrue("every built-in check runs on the SAMPLE data");

        var signed = await service.SignOffAsync(created.Id, new CdsSignOffRequest("Dr A. Patel", "Pharmacy review", created.ContentVersion), CancellationToken.None);
        signed.IsClinicallySignedOff.Should().BeTrue();
        signed.SignedOffBy.Should().Be("Dr A. Patel");
        signed.SignOffNote.Should().Be("Pharmacy review");
        signed.SignedOffAtUtc.Should().NotBeNull();

        // Switching it off (the list's toggle resends the whole service) keeps the sign-off.
        var toggled = await service.UpdateAsync(created.Id, SaveRequest(isEnabled: false), CancellationToken.None);
        toggled.IsClinicallySignedOff.Should().BeTrue();

        var changed = await service.UpdateAsync(
            created.Id, SaveRequest(settings: new JsonObject { ["allergyIndicator"] = "critical" }), CancellationToken.None);
        changed.IsClinicallySignedOff.Should().BeFalse();

        changed.ContentVersion.Should().NotBe(created.ContentVersion);
        await service.Invoking(s => s.SignOffAsync(created.Id, new CdsSignOffRequest("Dr B", null, created.ContentVersion), CancellationToken.None))
            .Should().ThrowAsync<CdsContentChangedException>("the reviewer saw the content before the settings change");
        await service.Invoking(s => s.SignOffAsync(created.Id, new CdsSignOffRequest("Dr B", null), CancellationToken.None))
            .Should().ThrowAsync<BusinessRuleException>("the content version is required");
        await service.SignOffAsync(created.Id, new CdsSignOffRequest("Dr B", null, changed.ContentVersion), CancellationToken.None);
        var cleared = await service.ClearSignOffAsync(created.Id, CancellationToken.None);
        cleared.IsClinicallySignedOff.Should().BeFalse();
        cleared.SignedOffBy.Should().BeNull();
    }

    [Fact]
    public async Task Sign_off_of_an_unknown_service_is_not_found()
    {
        await InstanceService().Invoking(s => s.SignOffAsync(Guid.NewGuid(), new CdsSignOffRequest("Dr A", null), CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_setup_sheet_shows_the_sign_off_or_warns_that_there_is_none()
    {
        var instance = Instance();
        await _instances.AddAsync(instance, CancellationToken.None);

        var before = await SetupSheet().GetAsync(instance.Id, "https://admin.example.org", CancellationToken.None);
        before.IsClinicallySignedOff.Should().BeFalse();
        before.Warnings.Should().Contain(w => w.StartsWith("Not clinically reviewed", StringComparison.Ordinal));

        instance.SignOff("Dr A", "Reviewed with Epic team", Now);
        var after = await SetupSheet().GetAsync(instance.Id, "https://admin.example.org", CancellationToken.None);
        after.IsClinicallySignedOff.Should().BeTrue();
        after.SignedOffBy.Should().Be("Dr A");
        after.SignedOffAtUtc.Should().Be(Now);
        after.SignOffNote.Should().Be("Reviewed with Epic team");
        after.Warnings.Should().NotContain(w => w.StartsWith("Not clinically reviewed", StringComparison.Ordinal));
    }

    // ---- Readiness rules ----

    [Fact]
    public void A_fully_prepared_deployment_is_ok_on_every_check()
    {
        var instance = Instance(fixes: Fixes());
        instance.SignOff("Dr A", null, Now);

        var report = Evaluate(Inputs(services: [instance]));

        report.Status.Should().Be(CdsReadinessStatus.Ok);
        report.Checks.Should().OnlyContain(c => c.Status == CdsReadinessStatus.Ok && c.HowToFix == null);
        report.Checks.Select(c => c.Key).Should().Equal(
            CdsReadinessCheckKeys.PublicBaseUrl,
            CdsReadinessCheckKeys.ReplayProtection,
            CdsReadinessCheckKeys.TrustedIssuer,
            CdsReadinessCheckKeys.IssuerJwksUrl,
            CdsReadinessCheckKeys.IssuerSubject,
            CdsReadinessCheckKeys.IssuerFhirBaseUrl,
            CdsReadinessCheckKeys.EnabledService,
            CdsReadinessCheckKeys.FixesSignOff,
            CdsReadinessCheckKeys.SampleDataSignOff,
            CdsReadinessCheckKeys.License,
            CdsReadinessCheckKeys.RateLimit);
        report.GeneratedOnUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData(null, false, CdsReadinessStatus.Blocker)]
    [InlineData("", false, CdsReadinessStatus.Blocker)]
    [InlineData("not a url", false, CdsReadinessStatus.Blocker)]
    [InlineData("http://segue.example.org", false, CdsReadinessStatus.Blocker)]
    [InlineData("http://localhost:5000", true, CdsReadinessStatus.Warning)]
    [InlineData("https://segue.example.org", false, CdsReadinessStatus.Ok)]
    public void The_public_base_url_must_be_set_and_https_outside_development(string? baseUrl, bool development, string expected)
    {
        var check = Check(Evaluate(Inputs(publicBaseUrl: baseUrl, isDevelopment: development)), CdsReadinessCheckKeys.PublicBaseUrl);

        check.Status.Should().Be(expected);
        if (expected != CdsReadinessStatus.Ok)
        {
            check.HowToFix.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void In_memory_replay_protection_is_a_warning()
    {
        var check = Check(Evaluate(Inputs(replayShared: false)), CdsReadinessCheckKeys.ReplayProtection);

        check.Status.Should().Be(CdsReadinessStatus.Warning);
        check.HowToFix.Should().Contain("ConnectionStrings:Redis");
    }

    [Fact]
    public void No_enabled_issuer_is_a_blocker_and_the_per_issuer_checks_are_left_out()
    {
        var report = Evaluate(Inputs(issuers: [Issuer(isEnabled: false)]));

        Check(report, CdsReadinessCheckKeys.TrustedIssuer).Status.Should().Be(CdsReadinessStatus.Blocker);
        report.Status.Should().Be(CdsReadinessStatus.Blocker);
        report.Checks.Select(c => c.Key).Should().NotContain(
            [CdsReadinessCheckKeys.IssuerJwksUrl, CdsReadinessCheckKeys.IssuerSubject, CdsReadinessCheckKeys.IssuerFhirBaseUrl]);
    }

    [Fact]
    public void An_epic_issuer_without_a_jwks_url_is_a_blocker_another_vendor_only_a_warning()
    {
        const string pasted = """{"keys":[{"kty":"EC","crv":"P-384","x":"x","y":"y"}]}""";

        var epic = Check(Evaluate(Inputs(issuers: [Issuer(name: "Epic prod", jwksUrl: null, jwksJson: pasted)])), CdsReadinessCheckKeys.IssuerJwksUrl);
        epic.Status.Should().Be(CdsReadinessStatus.Blocker);
        epic.Detail.Should().Contain("\"Epic prod\"").And.NotContain("P-384", "pasted keys are never echoed");

        var other = Check(
            Evaluate(Inputs(issuers: [Issuer(name: "Other EHR", vendor: "Other", jwksUrl: null, jwksJson: pasted)])),
            CdsReadinessCheckKeys.IssuerJwksUrl);
        other.Status.Should().Be(CdsReadinessStatus.Warning);
    }

    [Fact]
    public void An_issuer_without_an_expected_sub_or_an_https_fhir_base_is_a_warning()
    {
        var report = Evaluate(Inputs(issuers:
        [
            Issuer(name: "No sub", expectedSubject: null),
            Issuer(name: "Plain http", issuer: "https://other.example.org/fhir", fhirBaseUrl: "http://other.example.org/fhir"),
            Issuer(name: "No callbacks", issuer: "urn:not-a-url", allowCallback: false),
        ]));

        var sub = Check(report, CdsReadinessCheckKeys.IssuerSubject);
        sub.Status.Should().Be(CdsReadinessStatus.Warning);
        sub.Detail.Should().Contain("\"No sub\"").And.NotContain("Plain http");

        var fhirBase = Check(report, CdsReadinessCheckKeys.IssuerFhirBaseUrl);
        fhirBase.Status.Should().Be(CdsReadinessStatus.Warning);
        fhirBase.Detail.Should().Contain("\"Plain http\"").And.NotContain("No callbacks", "an issuer that allows no callbacks needs no FHIR base");
    }

    [Fact]
    public void No_service_switched_on_is_a_blocker()
    {
        var check = Check(Evaluate(Inputs(services: [Instance(isEnabled: false)])), CdsReadinessCheckKeys.EnabledService);

        check.Status.Should().Be(CdsReadinessStatus.Blocker);
    }

    [Fact]
    public void Services_without_a_clinical_sign_off_are_warnings_never_blockers()
    {
        var withFixes = Instance(serviceId: "allergy-fixes", title: "Allergy with fixes", fixes: Fixes());
        var plain = Instance(serviceId: "allergy-plain", title: "Allergy plain");

        var report = Evaluate(Inputs(services: [withFixes, plain]));

        var fixes = Check(report, CdsReadinessCheckKeys.FixesSignOff);
        fixes.Status.Should().Be(CdsReadinessStatus.Warning);
        fixes.Detail.Should().Contain("\"Allergy with fixes\"").And.NotContain("Allergy plain");

        var sample = Check(report, CdsReadinessCheckKeys.SampleDataSignOff);
        sample.Status.Should().Be(CdsReadinessStatus.Warning);
        sample.Detail.Should().Contain("\"Allergy with fixes\"").And.Contain("\"Allergy plain\"");
        report.Status.Should().Be(CdsReadinessStatus.Warning);

        withFixes.SignOff("Dr A", null, Now);
        plain.SignOff("Dr A", null, Now);
        var signed = Evaluate(Inputs(services: [withFixes, plain]));
        Check(signed, CdsReadinessCheckKeys.FixesSignOff).Status.Should().Be(CdsReadinessStatus.Ok);
        Check(signed, CdsReadinessCheckKeys.SampleDataSignOff).Status.Should().Be(CdsReadinessStatus.Ok);
    }

    [Fact]
    public void A_check_whose_handler_uses_its_own_reference_data_needs_no_sample_data_sign_off()
    {
        var handler = new Mock<ICdsServiceHandler>();
        handler.SetupGet(h => h.Key).Returns("local-rules");
        handler.SetupGet(h => h.DisplayName).Returns("Local rules");
        var registry = new CdsServiceHandlerRegistry([handler.Object]);
        var instance = new CdsServiceInstance(
            "local-rules", "Local rules", "local-rules", CdsHookNames.OrderSign, "Local.", true, null, "Segue", null, null);

        var check = Check(Evaluate(Inputs(services: [instance], handlers: registry)), CdsReadinessCheckKeys.SampleDataSignOff);

        check.Status.Should().Be(CdsReadinessStatus.Ok);
    }

    [Theory]
    [InlineData(LicenseState.Active, 200, CdsReadinessStatus.Ok)]
    [InlineData(LicenseState.Active, 10, CdsReadinessStatus.Warning)]
    [InlineData(LicenseState.Grace, null, CdsReadinessStatus.Warning)]
    [InlineData(LicenseState.Expired, null, CdsReadinessStatus.Blocker)]
    [InlineData(LicenseState.Unlicensed, null, CdsReadinessStatus.Blocker)]
    [InlineData(LicenseState.Invalid, null, CdsReadinessStatus.Blocker)]
    public void The_licence_must_be_active(LicenseState state, int? daysLeft, string expected)
    {
        var license = new LicenseStatus(
            state, "Hospital", "Enterprise", null, daysLeft is null ? null : DateTime.UtcNow.AddDays(daysLeft.Value).AddHours(1), null, [], null);

        Check(Evaluate(Inputs(license: license)), CdsReadinessCheckKeys.License).Status.Should().Be(expected);
    }

    [Theory]
    [InlineData(600, 1, CdsReadinessStatus.Ok)]
    [InlineData(30, 1, CdsReadinessStatus.Warning)]
    [InlineData(600, 60, CdsReadinessStatus.Warning)]
    [InlineData(0, 1, CdsReadinessStatus.Blocker)]
    [InlineData(600, 0, CdsReadinessStatus.Blocker)]
    public void The_rate_limit_must_be_positive_and_high_enough_for_an_ehr(int permit, int windowMinutes, string expected)
    {
        var check = Check(Evaluate(Inputs(ratePermit: permit, rateWindow: windowMinutes)), CdsReadinessCheckKeys.RateLimit);

        check.Status.Should().Be(expected);
        check.Detail.Should().NotBeNullOrWhiteSpace();
    }

    // ---- Readiness service: gathers the facts, reports no secrets ----

    [Fact]
    public async Task The_readiness_service_reads_the_deployment_and_never_reports_secrets()
    {
        const string redisSecret = "redis.example.org:6380,password=super-secret-redis,ssl=true";
        const string pastedJwks = """{"keys":[{"kty":"EC","crv":"P-384","x":"secret-x","y":"secret-y"}]}""";
        await _issuers.AddAsync(Issuer(jwksJson: pastedJwks), CancellationToken.None);
        await _instances.AddAsync(Instance(), CancellationToken.None);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CdsPublicBaseUrl.ConfigurationKey] = "https://segue.example.org",
                ["ConnectionStrings:Redis"] = redisSecret,
                [CdsReadinessService.RateLimitPermitKey] = "300",
            })
            .Build();
        var settings = new Mock<ISystemSettingsCache>();
        settings.Setup(s => s.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string fallback, CancellationToken _) => Task.FromResult(fallback));
        settings.Setup(s => s.GetIntAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int fallback, CancellationToken _) => Task.FromResult(fallback));
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");
        var license = new Mock<ILicenseService>();
        license.SetupGet(l => l.Current).Returns(new LicenseStatus(LicenseState.Active, "Hospital", null, null, null, null, [], null));
        var replayGuard = new CdsReplayGuard(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        var service = new CdsReadinessService(
            _instances, _issuers, _registry, new CdsPublicBaseUrlResolver(settings.Object, configuration), settings.Object,
            configuration, environment.Object, replayGuard, license.Object);
        var report = await service.GetAsync(CancellationToken.None);

        Check(report, CdsReadinessCheckKeys.PublicBaseUrl).Status.Should().Be(CdsReadinessStatus.Ok);
        Check(report, CdsReadinessCheckKeys.ReplayProtection).Status.Should().Be(CdsReadinessStatus.Warning, "this guard has no Redis connection");
        Check(report, CdsReadinessCheckKeys.License).Status.Should().Be(CdsReadinessStatus.Ok);
        Check(report, CdsReadinessCheckKeys.RateLimit).Detail.Should().StartWith("300 calls");
        var json = JsonSerializer.Serialize(report);
        json.Should().NotContain("super-secret-redis").And.NotContain("secret-x").And.NotContain("password");
    }

    // ---- Helpers ----

    private CdsReadinessDto Evaluate(CdsReadinessInputs inputs) => CdsReadinessRules.Evaluate(inputs, Now);

    private static CdsReadinessCheckDto Check(CdsReadinessDto report, string key) =>
        report.Checks.Should().ContainSingle(c => c.Key == key).Subject;

    private CdsReadinessInputs Inputs(
        string? publicBaseUrl = "https://segue.example.org",
        bool isDevelopment = false,
        bool replayShared = true,
        IReadOnlyList<CdsTrustedIssuer>? issuers = null,
        IReadOnlyList<CdsServiceInstance>? services = null,
        ICdsServiceHandlerRegistry? handlers = null,
        LicenseStatus? license = null,
        int ratePermit = 600,
        int rateWindow = 1)
    {
        IReadOnlyList<CdsServiceInstance> resolvedServices;
        if (services is null)
        {
            var signedOff = Instance();
            signedOff.SignOff("Dr A", null, Now);
            resolvedServices = [signedOff];
        }
        else
        {
            resolvedServices = services;
        }

        return new CdsReadinessInputs(
            publicBaseUrl,
            isDevelopment,
            replayShared,
            issuers ?? [Issuer()],
            resolvedServices,
            handlers ?? _registry,
            license ?? new LicenseStatus(LicenseState.Active, "Hospital", "Enterprise", null, null, null, [], null),
            ratePermit,
            rateWindow);
    }

    private static CdsTrustedIssuer Issuer(
        string name = "Epic prod",
        string vendor = "Epic",
        string issuer = EpicIss,
        string? jwksUrl = EpicJwks,
        string? jwksJson = null,
        string? expectedSubject = "epic-cds-client",
        string? fhirBaseUrl = null,
        bool allowCallback = true,
        bool isEnabled = true) =>
        new(name, vendor, issuer, jwksUrl, jwksJson, null, isEnabled, expectedSubject, fhirBaseUrl, allowCallback);

    private static CdsServiceInstance Instance(
        string serviceId = "epic-drug-allergy",
        string title = "Drug allergy",
        bool isEnabled = true,
        string? settingsJson = null,
        CdsFixSettings? fixes = null) =>
        new(serviceId, title, CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Checks allergies.", isEnabled, settingsJson, "Segue", null, null,
            fetchMissingData: true, fixes: fixes);

    private static CdsFixSettings Fixes() =>
        new(true, [new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, [])], CdsFixTypeKeys.RemoveDraftOrder);

    private static CdsOverrideReasonOption[] Reasons() => [new CdsOverrideReasonOption("pt_declined", "Patient declined")];

    private static CdsCardLinkOption[] Links() =>
        [new CdsCardLinkOption("Guideline", "https://guides.example.org/allergy", CdsCardLinkTypes.Absolute)];

    private SaveCdsServiceInstanceRequest SaveRequest(bool isEnabled = true, JsonObject? settings = null) =>
        new("svc-allergy", "Drug allergy", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, null, isEnabled, settings, null, null, null);

    private CdsServiceInstanceService InstanceService()
    {
        var names = new Mock<IUserDisplayNameResolver>();
        names.Setup(n => n.ResolveAsync(It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        return new CdsServiceInstanceService(_instances, _issuers, _registry, names.Object);
    }

    private CdsSetupSheetService SetupSheet()
    {
        var settings = new Mock<ISystemSettingsCache>();
        settings.Setup(s => s.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string fallback, CancellationToken _) => Task.FromResult(fallback));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CdsPublicBaseUrl.ConfigurationKey] = "https://segue.example.org" })
            .Build();
        return new CdsSetupSheetService(_instances, _issuers, _registry, new CdsPublicBaseUrlResolver(settings.Object, configuration));
    }
}
