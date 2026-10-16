using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The Epic setup sheet (every value the health system configures by hand), the issuer "Test callback" service, and
/// the admin save rules for fixes (Phase 2b), dismiss reasons (2c) and links (2d).
/// </summary>
public sealed class CdsSetupSheetAndAdminTests
{
    private const string PublicBase = "https://segue.example.org";
    private const string EpicIss = "https://ehr.example.org/api/FHIR/R4";

    private readonly InMemoryCdsServiceInstanceRepository _instances = new();
    private readonly InMemoryCdsTrustedIssuerRepository _issuers = new();
    private readonly CdsServiceHandlerRegistry _registry =
        new([new DrugAllergyCdsHandler(), new DrugInteractionCdsHandler(), new RenalDoseCdsHandler(), new CareGapCdsHandler(), new PriorAuthCdsHandler()]);

    private CdsSetupSheetService SetupSheet(string? publicBaseUrl = PublicBase)
    {
        var settings = new Mock<ISystemSettingsCache>();
        settings.Setup(s => s.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string fallback, CancellationToken _) => Task.FromResult(fallback));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CdsPublicBaseUrl.ConfigurationKey] = publicBaseUrl })
            .Build();
        return new CdsSetupSheetService(_instances, _issuers, _registry, new CdsPublicBaseUrlResolver(settings.Object, configuration));
    }

    private CdsServiceInstanceService InstanceService()
    {
        var names = new Mock<IUserDisplayNameResolver>();
        names.Setup(n => n.ResolveAsync(It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        return new CdsServiceInstanceService(_instances, _issuers, _registry, names.Object);
    }

    private async Task<CdsTrustedIssuer> AddIssuerAsync(
        string name = "Epic prod",
        string issuer = EpicIss,
        string? jwksUrl = "https://ehr.example.org/api/epic/2019/Security/Open/PublicKeys/1/OAuth2CertsJWKS",
        string? jwksJson = null,
        string? expectedSubject = "epic-cds-client",
        string? audience = null,
        string? fhirBaseUrl = null,
        bool allowCallback = true,
        bool isEnabled = true)
    {
        var trusted = new CdsTrustedIssuer(name, "Epic", issuer, jwksUrl, jwksJson, audience, isEnabled, expectedSubject, fhirBaseUrl, allowCallback);
        await _issuers.AddAsync(trusted, CancellationToken.None);
        return trusted;
    }

    private async Task<CdsServiceInstance> AddInstanceAsync(
        string serviceId = "epic-drug-allergy",
        bool enabled = true,
        IEnumerable<Guid>? allowedIssuerIds = null,
        bool fetchMissingData = true,
        CdsFixSettings? fixes = null,
        IEnumerable<CdsOverrideReasonOption>? reasons = null,
        IEnumerable<CdsCardLinkOption>? links = null,
        string handlerKey = CdsHandlerKeys.DrugAllergy,
        string hook = CdsHookNames.OrderSign)
    {
        var instance = new CdsServiceInstance(
            serviceId, "Drug allergy", handlerKey, hook, "Checks allergies.", enabled, null, "Segue", null, allowedIssuerIds,
            fetchMissingData, fixes, reasons, links);
        await _instances.AddAsync(instance, CancellationToken.None);
        return instance;
    }

    // ---- Setup sheet ----

    [Fact]
    public async Task The_setup_sheet_lists_every_value_epic_must_configure()
    {
        var epic = await AddIssuerAsync(fhirBaseUrl: "https://fhir.ehr.example.org/R4");
        await AddIssuerAsync(name: "Other EHR", issuer: "https://other.example.org");
        var fixes = new CdsFixSettings(
            true,
            [
                new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, []),
                new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
                [
                    new CdsFixCodeMapping("penicillin", CdsFixCodeSystems.EpicPreferenceListItem, "PL-100", "AZITHROMYCIN 250 MG"),
                    new CdsFixCodeMapping(null, CdsFixCodeSystems.RxNormOid, "18631", "azithromycin"),
                ]),
                new CdsFixTypeSetting(CdsFixTypeKeys.AddProblem, false, []),
            ],
            CdsFixTypeKeys.RemoveDraftOrder);
        var instance = await AddInstanceAsync(
            allowedIssuerIds: [epic.Id],
            fixes: fixes,
            reasons: [new CdsOverrideReasonOption("pt_declined", "Patient declined")],
            links:
            [
                new CdsCardLinkOption("Guideline", "https://guides.example.org/allergy", CdsCardLinkTypes.Absolute),
                new CdsCardLinkOption("Allergy app", "https://app.example.org/launch", CdsCardLinkTypes.Smart, "ctx", true),
            ]);

        var sheet = await SetupSheet().GetAsync(instance.Id, "https://admin-browser.example.org", CancellationToken.None);

        sheet.ServiceUrl.Should().Be($"{PublicBase}/cds-services/epic-drug-allergy");
        sheet.FeedbackUrl.Should().Be($"{PublicBase}/cds-services/epic-drug-allergy/feedback");
        sheet.DefaultAudience.Should().Be(sheet.ServiceUrl);
        sheet.PublicBaseUrlConfigured.Should().BeTrue();
        sheet.Hook.Should().Be(CdsHookNames.OrderSign);

        var prefetch = sheet.Prefetch.Should().ContainSingle().Subject;
        prefetch.Key.Should().Be("allergies");
        prefetch.Query.Should().Be("AllergyIntolerance?patient={{context.patientId}}");
        prefetch.FetchedIfMissing.Should().BeTrue();

        sheet.OverrideReasons.Should().ContainSingle().Which.Code.Should().Be("pt_declined");
        sheet.OverrideReasonSystem.Should().Be(CdsOverrideReasonOption.System);

        sheet.FixesEnabled.Should().BeTrue();
        sheet.Fixes.Should().HaveCount(3, "one row per mapping, one code-less row for the delete, nothing for a switched-off fix");
        var remove = sheet.Fixes.Single(f => f.FixTypeKey == CdsFixTypeKeys.RemoveDraftOrder);
        remove.Code.Should().BeNull();
        remove.ActionType.Should().Be(CdsActionTypes.Delete);
        remove.IsRecommended.Should().BeTrue();
        sheet.Fixes.Where(f => f.FixTypeKey == CdsFixTypeKeys.AddAlternativeMedication)
            .Select(f => (f.Trigger, f.System, f.Code))
            .Should().BeEquivalentTo(new[]
            {
                ("penicillin", CdsFixCodeSystems.EpicPreferenceListItem, "PL-100"),
                ((string?)null, CdsFixCodeSystems.RxNormOid, "18631"),
            });

        sheet.Links.Select(l => l.Type).Should().Equal(CdsCardLinkTypes.Absolute, CdsCardLinkTypes.Smart);

        var issuer = sheet.Issuers.Should().ContainSingle("only the service's allow-listed issuer").Subject;
        issuer.Issuer.Should().Be(EpicIss);
        issuer.ExpectedSubject.Should().Be("epic-cds-client");
        issuer.ExpectedAudience.Should().Be(sheet.ServiceUrl);
        issuer.EffectiveFhirBaseUrl.Should().Be("https://fhir.ehr.example.org/R4");
        issuer.JwksUrl.Should().NotBeNull();

        sheet.Warnings.Should().Contain(w => w.Contains("fixes are on", StringComparison.OrdinalIgnoreCase));
        sheet.Warnings.Should().Contain(w => w.Contains("SMART", StringComparison.Ordinal));
        sheet.Warnings.Should().NotContain(w => w.Contains("JWKS URL", StringComparison.Ordinal) || w.Contains("expected sub", StringComparison.Ordinal));
        sheet.Warnings.Should().NotContain(w => w.Contains(CdsPublicBaseUrl.ConfigurationKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_setup_sheet_warns_about_every_gap_in_the_configuration()
    {
        await AddIssuerAsync(jwksUrl: null, jwksJson: """{"keys":[{"kty":"EC","crv":"P-384","x":"x","y":"y"}]}""", expectedSubject: null);
        var instance = await AddInstanceAsync(enabled: false, fetchMissingData: false);

        var sheet = await SetupSheet(publicBaseUrl: null).GetAsync(instance.Id, "https://admin-browser.example.org", CancellationToken.None);

        sheet.PublicBaseUrlConfigured.Should().BeFalse();
        sheet.ServiceUrl.Should().StartWith("https://admin-browser.example.org/");
        sheet.Prefetch.Should().AllSatisfy(p => p.FetchedIfMissing.Should().BeFalse());
        sheet.Fixes.Should().BeEmpty();
        sheet.OverrideReasons.Should().BeEquivalentTo(CdsOverrideReasonOption.Defaults);
        sheet.Issuers.Single().HasPastedJwks.Should().BeTrue();
        sheet.Warnings.Should().Contain(w => w.Contains(CdsPublicBaseUrl.ConfigurationKey, StringComparison.Ordinal));
        sheet.Warnings.Should().Contain(w => w.Contains("switched off", StringComparison.Ordinal));
        sheet.Warnings.Should().Contain(w => w.Contains("no JWKS URL", StringComparison.Ordinal));
        sheet.Warnings.Should().Contain(w => w.Contains("no expected sub", StringComparison.Ordinal));
        sheet.Warnings.Should().Contain(w => w.Contains("must send every prefetch key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_an_issuer_that_allows_callbacks_no_prefetch_is_fetched_if_missing()
    {
        await AddIssuerAsync(allowCallback: false);
        var instance = await AddInstanceAsync();

        var sheet = await SetupSheet().GetAsync(instance.Id, PublicBase, CancellationToken.None);

        sheet.Prefetch.Should().AllSatisfy(p => p.FetchedIfMissing.Should().BeFalse());
        sheet.Issuers.Single().AllowCallback.Should().BeFalse();
    }

    [Fact]
    public async Task With_no_trusted_issuer_the_sheet_says_so()
    {
        var instance = await AddInstanceAsync();

        var sheet = await SetupSheet().GetAsync(instance.Id, PublicBase, CancellationToken.None);

        sheet.Issuers.Should().BeEmpty();
        sheet.Warnings.Should().Contain(w => w.Contains("No enabled trusted issuer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_issuer_audience_override_is_the_audience_shown_for_it()
    {
        await AddIssuerAsync(audience: "urn:segue:cds");
        var instance = await AddInstanceAsync();

        var sheet = await SetupSheet().GetAsync(instance.Id, PublicBase, CancellationToken.None);

        sheet.Issuers.Single().ExpectedAudience.Should().Be("urn:segue:cds");
    }

    [Fact]
    public async Task A_patient_view_service_lists_all_its_prefetch_keys()
    {
        await AddIssuerAsync();
        var instance = await AddInstanceAsync(serviceId: "epic-care-gap", handlerKey: CdsHandlerKeys.CareGap, hook: CdsHookNames.PatientView);

        var sheet = await SetupSheet().GetAsync(instance.Id, PublicBase, CancellationToken.None);

        sheet.Prefetch.Select(p => p.Key).Should().BeEquivalentTo("patient", "conditions", "hba1c", "immunizations", "procedures");
        sheet.Prefetch.Single(p => p.Key == "patient").Query.Should().Be("Patient/{{context.patientId}}");
    }

    [Fact]
    public async Task An_unknown_service_is_not_found()
    {
        var act = () => SetupSheet().GetAsync(Guid.NewGuid(), PublicBase, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_setup_sheet_serializes_with_nothing_but_configuration()
    {
        await AddIssuerAsync();
        var instance = await AddInstanceAsync();

        var json = JsonSerializer.Serialize(await SetupSheet().GetAsync(instance.Id, PublicBase, CancellationToken.None));

        json.Should().Contain("allergies").And.NotContain("access_token").And.NotContain("patientId\":");
    }

    // ---- Test callback ----

    [Fact]
    public async Task Test_callback_probes_the_issuer_fhir_base_and_says_when_callbacks_are_off()
    {
        var issuer = await AddIssuerAsync(fhirBaseUrl: "https://fhir.ehr.example.org/R4", allowCallback: false);
        var client = new Mock<ICdsFhirCallbackClient>();
        client.Setup(c => c.ProbeMetadataAsync("https://fhir.ehr.example.org/R4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CdsCallbackProbeResult(true, 200, "4.0.1", "Epic", 42, null));

        var result = await new CdsCallbackTestService(_issuers, client.Object).TestAsync(issuer.Id, CancellationToken.None);

        result.Reachable.Should().BeTrue();
        result.FhirBaseUrl.Should().Be("https://fhir.ehr.example.org/R4");
        result.AllowCallback.Should().BeFalse();
        result.Message.Should().Contain("Epic").And.Contain("FHIR 4.0.1").And.Contain("Callbacks are off");
        client.Verify(c => c.FetchAsync(It.IsAny<CdsCallbackRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Test_callback_reports_an_unreachable_server()
    {
        var issuer = await AddIssuerAsync();
        var client = new Mock<ICdsFhirCallbackClient>();
        client.Setup(c => c.ProbeMetadataAsync(EpicIss, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CdsCallbackProbeResult(false, null, null, null, 10, "The server could not be reached."));

        var result = await new CdsCallbackTestService(_issuers, client.Object).TestAsync(issuer.Id, CancellationToken.None);

        result.Reachable.Should().BeFalse();
        result.Message.Should().Contain("could not be reached");
    }

    [Fact]
    public async Task Test_callback_for_an_unknown_issuer_is_not_found()
    {
        var act = () => new CdsCallbackTestService(_issuers, Mock.Of<ICdsFhirCallbackClient>()).TestAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ---- Admin save rules: fixes ----

    private static SaveCdsServiceInstanceRequest Save(
        CdsFixSettings? fixes = null,
        IReadOnlyList<CdsOverrideReasonOption>? reasons = null,
        IReadOnlyList<CdsCardLinkOption>? links = null,
        string handlerKey = CdsHandlerKeys.DrugAllergy,
        string hook = CdsHookNames.OrderSign,
        bool fetchMissingData = true) =>
        new($"svc-{Guid.NewGuid():N}"[..20], "Service", handlerKey, hook, null, true, null, null, null, null,
            fetchMissingData, fixes, reasons, links);

    private static CdsFixSettings Fixes(params CdsFixTypeSetting[] types) => new(true, types);

    public static TheoryData<CdsFixSettings, string> InvalidFixes() => new()
    {
        { Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.AddTestOrder, true, [new CdsFixCodeMapping(null, CdsFixCodeSystems.EpicPreferenceListItem, "1", "x")])), "does not offer" },
        { Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true, [])), "at least one code" },
        { Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true, [new CdsFixCodeMapping(null, CdsFixCodeSystems.Icd10Cm, "1", "x")])), "code system" },
        { Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true, [new CdsFixCodeMapping("hba1c", CdsFixCodeSystems.RxNormOid, "1", "x")])), "not one of its triggers" },
        { Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.AddProblem, true, [new CdsFixCodeMapping(null, CdsFixCodeSystems.Icd10Cm, "Z88.0", null)])), "display name" },
        { Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, false, [])) with { RecommendedFixType = CdsFixTypeKeys.RemoveDraftOrder }, "recommended" },
        {
            Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
            [
                new CdsFixCodeMapping("penicillin", CdsFixCodeSystems.RxNormOid, "1", "x"),
                new CdsFixCodeMapping("penicillin", CdsFixCodeSystems.RxNormOid, "2", "y"),
            ])),
            "two mappings"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidFixes))]
    public async Task Invalid_fix_settings_are_refused_with_the_reason(CdsFixSettings fixes, string reason)
    {
        var act = () => InstanceService().CreateAsync(Save(fixes), CancellationToken.None);

        (await act.Should().ThrowAsync<BusinessRuleException>()).Which.Message.Should().Contain(reason);
        (await _instances.GetAllAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Valid_fixes_reasons_and_links_are_saved_and_returned()
    {
        var fixes = new CdsFixSettings(
            true,
            [
                new CdsFixTypeSetting(CdsFixTypeKeys.RemoveDraftOrder, true, []),
                new CdsFixTypeSetting(CdsFixTypeKeys.AddAlternativeMedication, true,
                    [new CdsFixCodeMapping("penicillin", CdsFixCodeSystems.EpicPreferenceListItem, "PL-1", "AZITHROMYCIN")]),
            ],
            CdsFixTypeKeys.AddAlternativeMedication);

        var saved = await InstanceService().CreateAsync(
            Save(
                fixes,
                [new CdsOverrideReasonOption(" pt_declined ", " Patient declined ")],
                [new CdsCardLinkOption("App", "https://app.example.org", "SMART", "ctx", true)],
                fetchMissingData: false),
            CancellationToken.None);

        var stored = (await _instances.GetAllAsync(CancellationToken.None)).Single();
        stored.FetchMissingData.Should().BeFalse();
        stored.FixesEnabled.Should().BeTrue();
        stored.GetFixes().RecommendedFixType.Should().Be(CdsFixTypeKeys.AddAlternativeMedication);
        stored.GetOverrideReasons().Single().Should().Be(new CdsOverrideReasonOption("pt_declined", "Patient declined"));
        stored.GetLinks().Single().Type.Should().Be(CdsCardLinkTypes.Smart);
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task A_care_gap_service_takes_a_test_order_mapping_keyed_by_gap()
    {
        var fixes = Fixes(new CdsFixTypeSetting(CdsFixTypeKeys.AddTestOrder, true,
            [new CdsFixCodeMapping("hba1c", CdsFixCodeSystems.EpicOrderSetItem, "OS-1", null)]));

        await InstanceService().CreateAsync(Save(fixes, handlerKey: CdsHandlerKeys.CareGap, hook: CdsHookNames.PatientView), CancellationToken.None);

        (await _instances.GetAllAsync(CancellationToken.None)).Single().GetFixes().FixTypes.Single().Mappings.Single().Trigger.Should().Be("hba1c");
    }

    // ---- Admin save rules: dismiss reasons ----

    [Fact]
    public async Task An_empty_reason_list_means_the_defaults()
    {
        await InstanceService().CreateAsync(Save(reasons: []), CancellationToken.None);

        var stored = (await _instances.GetAllAsync(CancellationToken.None)).Single();
        stored.OverrideReasonsJson.Should().BeNull();
        stored.GetOverrideReasons().Should().BeEquivalentTo(CdsOverrideReasonOption.Defaults);
    }

    [Theory]
    [InlineData("dup", "DUP")]
    [InlineData("a b", "ok")]
    [InlineData("ok", "x.y")]
    public async Task Duplicate_or_malformed_reason_codes_are_refused(string first, string second)
    {
        var act = () => InstanceService().CreateAsync(
            Save(reasons: [new CdsOverrideReasonOption(first, "One"), new CdsOverrideReasonOption(second, "Two")]), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public void A_reason_needs_display_text_and_a_code_of_at_most_50_characters()
    {
        ((Action)(() => CdsServiceOptionsRules.NormalizeOverrideReasons([new CdsOverrideReasonOption("code", " ")])))
            .Should().Throw<BusinessRuleException>();
        ((Action)(() => CdsServiceOptionsRules.NormalizeOverrideReasons([new CdsOverrideReasonOption(new string('a', 51), "Text")])))
            .Should().Throw<BusinessRuleException>();
        CdsServiceOptionsRules.NormalizeOverrideReasons(Enumerable.Range(1, 10).Select(i => new CdsOverrideReasonOption($"r-{i}", "Text")))
            .Should().HaveCount(10);
    }

    // ---- Admin save rules: links ----

    [Theory]
    [InlineData("Guide", "ftp://example.org/x", "absolute")]
    [InlineData("Guide", "/relative/path", "absolute")]
    [InlineData("Guide", "https://example.org", "popup")]
    [InlineData(" ", "https://example.org", "absolute")]
    public void A_link_must_be_https_labelled_and_absolute_or_smart(string label, string url, string type)
    {
        var act = () => CdsServiceOptionsRules.NormalizeLinks([new CdsCardLinkOption(label, url, type)]);

        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void At_most_ten_links()
    {
        var act = () => CdsServiceOptionsRules.NormalizeLinks(
            Enumerable.Range(0, 11).Select(i => new CdsCardLinkOption($"L{i}", "https://example.org", CdsCardLinkTypes.Absolute)));

        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void A_smart_link_may_leave_out_app_context_and_type_is_normalized()
    {
        var link = CdsServiceOptionsRules.NormalizeLinks([new CdsCardLinkOption(" App ", " https://app.example.org ", " Smart ")])!.Single();

        link.Should().Be(new CdsCardLinkOption("App", "https://app.example.org", CdsCardLinkTypes.Smart));
    }

    [Fact]
    public async Task An_invalid_link_is_refused_on_save()
    {
        var act = () => InstanceService().CreateAsync(
            Save(links: [new CdsCardLinkOption("Guide", "https://example.org", CdsCardLinkTypes.Absolute, AppContext: "ctx")]), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    // ---- Trusted issuer: Epic fields ----

    [Fact]
    public void An_issuer_keeps_sub_fhir_base_and_callback_switch_and_falls_back_to_iss_as_fhir_base()
    {
        var withBase = new CdsTrustedIssuer("Epic", "Epic", EpicIss, "https://ehr.example.org/jwks", null, null, true,
            " epic-client ", "https://fhir.ehr.example.org/R4/", allowCallback: false);
        var withoutBase = new CdsTrustedIssuer("Epic", "Epic", EpicIss, "https://ehr.example.org/jwks", null, null, true);

        withBase.ExpectedSubject.Should().Be("epic-client");
        withBase.FhirBaseUrl.Should().Be("https://fhir.ehr.example.org/R4");
        withBase.AllowCallback.Should().BeFalse();
        withoutBase.EffectiveFhirBaseUrl.Should().Be(EpicIss);
        withoutBase.AllowCallback.Should().BeTrue();
        withoutBase.IsTrustedFhirServer(EpicIss + "/").Should().BeTrue();
        withoutBase.IsTrustedJku(null).Should().BeFalse();
    }
}
