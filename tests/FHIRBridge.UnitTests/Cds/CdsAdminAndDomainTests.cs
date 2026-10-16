using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// Domain invariants of the two CDS configuration entities (service id slug, required fields, key material) and
/// the admin services' cross-row rules (unique service id and iss, known handler, supported hook, settings).
/// </summary>
public sealed class CdsAdminAndDomainTests
{
    private const string Jwks = """{"keys":[{"kty":"EC","crv":"P-384","x":"x","y":"y"}]}""";

    private readonly InMemoryCdsServiceInstanceRepository _instances = new();
    private readonly InMemoryCdsTrustedIssuerRepository _issuers = new();

    private static CdsServiceInstance Instance(string serviceId) =>
        new(serviceId, "Title", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Description", true, null, "Label", null, null);

    private CdsServiceInstanceService InstanceService()
    {
        var names = new Mock<IUserDisplayNameResolver>();
        names.Setup(n => n.ResolveAsync(It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var registry = new CdsServiceHandlerRegistry([new DrugAllergyCdsHandler(), new CareGapCdsHandler(), new RenalDoseCdsHandler()]);
        return new CdsServiceInstanceService(_instances, _issuers, registry, names.Object);
    }

    private CdsTrustedIssuerService IssuerService()
    {
        var names = new Mock<IUserDisplayNameResolver>();
        names.Setup(n => n.ResolveAsync(It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        return new CdsTrustedIssuerService(_issuers, _instances, names.Object);
    }

    private static SaveCdsServiceInstanceRequest SaveRequest(
        string serviceId = "drug-allergy",
        string handlerKey = CdsHandlerKeys.DrugAllergy,
        string hook = CdsHookNames.OrderSign,
        JsonObject? settings = null,
        IReadOnlyList<Guid>? allowedIssuerIds = null) =>
        new(serviceId, "Drug allergy", handlerKey, hook, null, true, settings, null, null, allowedIssuerIds);

    // ---- CdsServiceInstance ----

    [Theory]
    [InlineData("drug-allergy", "drug-allergy")]
    [InlineData("  Drug-Allergy-2 ", "drug-allergy-2")]
    [InlineData("abc", "abc")]
    public void A_valid_service_id_is_normalized_to_a_lower_case_slug(string input, string expected)
    {
        Instance(input).ServiceId.Should().Be(expected);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("drug allergy")]
    [InlineData("drug--allergy")]
    [InlineData("-drug")]
    [InlineData("drug-")]
    [InlineData("drug_allergy")]
    [InlineData("drug/allergy")]
    [InlineData("../etc")]
    public void An_invalid_service_id_is_rejected(string serviceId)
    {
        var act = () => Instance(serviceId);

        act.Should().Throw<BusinessRuleException>().WithMessage("Service id*");
    }

    [Fact]
    public void A_service_id_longer_than_100_characters_is_rejected()
    {
        var act = () => Instance(new string('a', CdsServiceInstance.ServiceIdMaxLength + 1));

        act.Should().Throw<BusinessRuleException>();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{not json")]
    public void Settings_must_be_a_json_object(string settingsJson)
    {
        var act = () => new CdsServiceInstance(
            "drug-allergy", "Title", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Description", true, settingsJson, "Label", null, null);

        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void A_card_source_url_must_be_absolute_http()
    {
        var act = () => new CdsServiceInstance(
            "drug-allergy", "Title", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Description", true, null, "Label", "javascript:alert(1)", null);

        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void An_empty_allow_list_admits_any_issuer_and_a_filled_one_only_its_members()
    {
        var allowed = Guid.NewGuid();
        var open = Instance("open-service");
        var restricted = new CdsServiceInstance(
            "restricted", "Title", CdsHandlerKeys.DrugAllergy, CdsHookNames.OrderSign, "Description", true, null, "Label", null,
            [allowed, allowed, Guid.Empty]);

        open.AllowsIssuer(Guid.NewGuid()).Should().BeTrue();
        restricted.AllowedIssuerIds.Should().Equal(allowed);
        restricted.AllowsIssuer(allowed).Should().BeTrue();
        restricted.AllowsIssuer(Guid.NewGuid()).Should().BeFalse();
    }

    // ---- CdsTrustedIssuer ----

    [Fact]
    public void A_trusted_issuer_needs_a_jwks_url_or_a_pasted_jwks()
    {
        var act = () => new CdsTrustedIssuer("Sandbox", "Other", "https://sandbox.cds-hooks.org", null, null, null, true);

        act.Should().Throw<BusinessRuleException>().WithMessage("*JWKS*");
    }

    [Theory]
    [InlineData("""{"keys":[]}""")]
    [InlineData("""{"nokeys":1}""")]
    [InlineData("""[1,2]""")]
    [InlineData("not json")]
    public void A_pasted_jwks_must_hold_a_non_empty_keys_array(string jwks)
    {
        var act = () => new CdsTrustedIssuer("Sandbox", "Other", "https://sandbox.cds-hooks.org", null, jwks, null, true);

        act.Should().Throw<BusinessRuleException>();
    }

    [Theory]
    [InlineData("file:///etc/keys")]
    [InlineData("http://ehr.example.org/jwks.json")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public void A_jwks_url_must_be_absolute_https(string jwksUrl)
    {
        var act = () => new CdsTrustedIssuer("Sandbox", "Other", "https://sandbox.cds-hooks.org", jwksUrl, null, null, true);

        act.Should().Throw<BusinessRuleException>().WithMessage("*https*");
    }

    // ---- Admin services ----

    [Fact]
    public async Task A_service_id_must_be_unique_among_non_deleted_instances()
    {
        var service = InstanceService();
        await service.CreateAsync(SaveRequest(), CancellationToken.None);

        var duplicate = () => service.CreateAsync(SaveRequest(serviceId: "DRUG-ALLERGY"), CancellationToken.None);

        await duplicate.Should().ThrowAsync<BusinessRuleException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task A_deleted_instance_frees_its_service_id()
    {
        var service = InstanceService();
        var first = await service.CreateAsync(SaveRequest(), CancellationToken.None);
        await service.DeleteAsync(first.Id, CancellationToken.None);

        var second = await service.CreateAsync(SaveRequest(), CancellationToken.None);

        second.ServiceId.Should().Be("drug-allergy");
    }

    [Fact]
    public async Task Updating_an_instance_may_keep_its_own_service_id()
    {
        var service = InstanceService();
        var created = await service.CreateAsync(SaveRequest(), CancellationToken.None);

        var updated = await service.UpdateAsync(created.Id, SaveRequest(), CancellationToken.None);

        updated.ServiceId.Should().Be("drug-allergy");
    }

    [Fact]
    public async Task An_unknown_handler_type_is_rejected()
    {
        var act = () => InstanceService().CreateAsync(SaveRequest(handlerKey: "no-such-handler"), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>().WithMessage("Unknown handler type*");
    }

    [Fact]
    public async Task A_hook_the_handler_does_not_support_is_rejected()
    {
        var act = () => InstanceService().CreateAsync(SaveRequest(hook: CdsHookNames.PatientView), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>().WithMessage("*order-select, order-sign*");
    }

    [Fact]
    public async Task Settings_are_validated_against_the_handler_descriptor()
    {
        var service = InstanceService();

        var unknownKey = () => service.CreateAsync(SaveRequest(settings: new JsonObject { ["bogus"] = 1 }), CancellationToken.None);
        var outOfRange = () => service.CreateAsync(
            SaveRequest("renal", CdsHandlerKeys.RenalDose, settings: new JsonObject { ["egfrThreshold"] = 500 }), CancellationToken.None);
        var badSelect = () => service.CreateAsync(
            SaveRequest(settings: new JsonObject { ["allergyIndicator"] = "hard-stop" }), CancellationToken.None);

        await unknownKey.Should().ThrowAsync<BusinessRuleException>().WithMessage("*'bogus'*");
        await outOfRange.Should().ThrowAsync<BusinessRuleException>().WithMessage("*between 5 and 120*");
        await badSelect.Should().ThrowAsync<BusinessRuleException>().WithMessage("*one of*");
    }

    [Fact]
    public async Task Saved_settings_are_returned_with_defaults_filled_in()
    {
        var created = await InstanceService().CreateAsync(
            SaveRequest("renal", CdsHandlerKeys.RenalDose, settings: new JsonObject { ["egfrThreshold"] = 30 }), CancellationToken.None);

        created.Settings["egfrThreshold"]!.ToJsonString().Should().Be("30");
        created.Settings["maxResultAgeDays"]!.ToJsonString().Should().Be("365");
        created.Prefetch.Should().ContainKey("egfr");
    }

    [Fact]
    public async Task An_allow_list_naming_an_unknown_issuer_is_rejected()
    {
        var act = () => InstanceService().CreateAsync(SaveRequest(allowedIssuerIds: [Guid.NewGuid()]), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task An_issuer_iss_must_be_unique()
    {
        var service = IssuerService();
        await service.CreateAsync(new SaveCdsTrustedIssuerRequest("A", "Other", "https://sandbox.cds-hooks.org", null, Jwks, null, true), CancellationToken.None);

        var act = () => service.CreateAsync(
            new SaveCdsTrustedIssuerRequest("B", "Other", "https://sandbox.cds-hooks.org", null, Jwks, null, true), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task An_issuer_on_an_allow_list_cannot_be_deleted()
    {
        var issuer = await IssuerService().CreateAsync(
            new SaveCdsTrustedIssuerRequest("A", "Other", "https://sandbox.cds-hooks.org", null, Jwks, null, true), CancellationToken.None);
        await InstanceService().CreateAsync(SaveRequest(allowedIssuerIds: [issuer.Id]), CancellationToken.None);

        var act = () => IssuerService().DeleteAsync(issuer.Id, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>().WithMessage("*allow-list*");
    }
}
