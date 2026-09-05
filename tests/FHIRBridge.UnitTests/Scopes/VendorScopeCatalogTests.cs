using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Scopes;

/// <summary>
/// Pins the per-vendor <c>system/</c> scope vocabulary (see <see cref="VendorScopeCatalog"/>) and, just as
/// importantly, pins that it changes NOTHING for any vendor without a registered profile. Every expectation about
/// eClinicalWorks below is taken from the practice's own live
/// <c>/.well-known/smart-configuration</c> <c>scopes_supported</c> (FFBJCD staging sandbox, 165 <c>system/</c>
/// scopes), not from the generic SMART spelling.
/// </summary>
public sealed class VendorScopeCatalogTests
{
    private readonly ScopeGeneratorService _sut = new();

    // ── The bug this exists to prevent ────────────────────────────────────────
    // eCW publishes system/ServiceRequest.r and NO system/ServiceRequest.read. ServiceRequest is in FHIRBridge's
    // own MVP1 resource set, and eCW fails the WHOLE token request on one unrecognized scope — so the uniform
    // '.read' suffix cost every other scope in the request too.
    [Fact]
    public void EClinicalWorks_backend_spells_ServiceRequest_with_the_short_form_it_actually_publishes()
    {
        var result = _sut.Generate(
            ApplicationType.Backend, ["ServiceRequest"], "v1", false, null, SourceSystemType.Healow);

        result.Scopes.Should().ContainSingle().Which.Should().Be("system/ServiceRequest.r");
        result.Scopes.Should().NotContain("system/ServiceRequest.read");
    }

    [Theory]
    // The nine resource types eCW exposes ONLY as '.r'.
    [InlineData("Binary")]
    [InlineData("Claim")]
    [InlineData("Coverage")]
    [InlineData("Media")]
    [InlineData("MedicationDispense")]
    [InlineData("QuestionnaireResponse")]
    [InlineData("RelatedPerson")]
    [InlineData("ServiceRequest")]
    [InlineData("Specimen")]
    public void EClinicalWorks_short_form_only_resources_use_r(string resourceType)
    {
        var result = _sut.Generate(
            ApplicationType.Backend, [resourceType], "v1", false, null, SourceSystemType.Healow);

        result.Scopes.Should().ContainSingle().Which.Should().Be($"system/{resourceType}.r");
    }

    [Theory]
    // A sample of the 24 resource types eCW does expose as '.read'.
    [InlineData("Patient")]
    [InlineData("Observation")]
    [InlineData("Condition")]
    [InlineData("MedicationRequest")]
    [InlineData("CarePlan")]
    [InlineData("Provenance")]
    public void EClinicalWorks_coarse_resources_use_read(string resourceType)
    {
        var result = _sut.Generate(
            ApplicationType.Backend, [resourceType], "v1", false, null, SourceSystemType.Healow);

        result.Scopes.Should().ContainSingle().Which.Should().Be($"system/{resourceType}.read");
    }

    [Fact]
    public void EClinicalWorks_ignores_the_requested_scope_version_and_uses_its_own_vocabulary()
    {
        // Even asked for v2, eCW gets what it publishes: '.read' for Patient (it has no Patient.rs preference here)
        // and '.r' for ServiceRequest — never a blanket '.rs'.
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient", "ServiceRequest"], "v2", true, null, SourceSystemType.Healow);

        result.Scopes.Should().BeEquivalentTo(["system/Patient.read", "system/ServiceRequest.r"]);
    }

    [Theory]
    // Resource types in SUPPORTED_RESOURCE_TYPES that eCW advertises no system read scope for at all, plus
    // FamilyMemberHistory, which eCW exposes as create/update only.
    [InlineData("Appointment")]
    [InlineData("Schedule")]
    [InlineData("Slot")]
    [InlineData("Task")]
    [InlineData("Consent")]
    [InlineData("FamilyMemberHistory")]
    public void EClinicalWorks_omits_resources_it_publishes_no_read_scope_for_and_reports_them(string resourceType)
    {
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient", resourceType], "v1", false, null, SourceSystemType.Healow);

        result.Scopes.Should().ContainSingle().Which.Should().Be("system/Patient.read");
        result.UnsupportedScopes.Should().ContainSingle().Which.Should().Be($"system/{resourceType}.read");
        // The profile is itself a record of what the server publishes, so the result counts as validated.
        result.ValidatedAgainstDiscovery.Should().BeTrue();
    }

    // eCW's Backend Authentication guide: system/Group.read is required for bulk (Group $export) requests and must
    // be EXCLUDED from Backend Single Patient calls — the only eCW backend flow FHIRBridge supports.
    [Fact]
    public void EClinicalWorks_never_requests_Group_even_though_the_server_advertises_it()
    {
        var result = _sut.Generate(
            ApplicationType.Backend, ["Group", "Patient"], "v1", false, null, SourceSystemType.Healow);

        result.Scopes.Should().ContainSingle().Which.Should().Be("system/Patient.read");
        result.ScopeString.Should().NotContain("Group");
    }

    // ── Isolation: the profile is system/-only and vendor-only ────────────────
    [Theory]
    [InlineData(ApplicationType.Patient, "patient")]
    [InlineData(ApplicationType.Standalone, "user")]
    [InlineData(ApplicationType.EhrLaunch, "user")]
    public void EClinicalWorks_interactive_audiences_keep_the_uniform_suffix_untouched(
        ApplicationType applicationType,
        string expectedPrefix)
    {
        // The profile deliberately governs system/ only, so eCW's already-verified Patient and EHR-launch scope
        // generation is byte-identical to before it existed — including for ServiceRequest.
        var result = _sut.Generate(
            applicationType, ["ServiceRequest"], "v1", false, null, SourceSystemType.Healow);

        result.Scopes.Should().Contain($"{expectedPrefix}/ServiceRequest.read");
    }

    [Theory]
    [InlineData(SourceSystemType.Epic)]
    [InlineData(SourceSystemType.Cerner)]
    [InlineData(SourceSystemType.Athenahealth)]
    [InlineData(SourceSystemType.MeditechGreenfield)]
    [InlineData(SourceSystemType.GenericFhir)]
    public void Every_other_vendor_keeps_the_uniform_suffix(SourceSystemType vendor)
    {
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient", "ServiceRequest", "Appointment"], "v1", false, null, vendor);

        result.Scopes.Should().BeEquivalentTo(
            ["system/Appointment.read", "system/Patient.read", "system/ServiceRequest.read"]);
        result.UnsupportedScopes.Should().BeEmpty();
        result.ValidatedAgainstDiscovery.Should().BeFalse();
    }

    [Fact]
    public void Omitting_the_vendor_argument_is_the_pre_existing_behaviour()
    {
        var withoutVendor = _sut.Generate(ApplicationType.Backend, ["ServiceRequest"], "v1", false, null);
        var explicitlyNoVendor = _sut.Generate(
            ApplicationType.Backend, ["ServiceRequest"], "v1", false, null, vendor: null);

        withoutVendor.ScopeString.Should().Be("system/ServiceRequest.read");
        explicitlyNoVendor.ScopeString.Should().Be(withoutVendor.ScopeString);
    }

    [Fact]
    public void Catalog_only_registers_eClinicalWorks()
    {
        VendorScopeCatalog.For(SourceSystemType.Healow).Should().NotBeNull();
        VendorScopeCatalog.For(null).Should().BeNull();

        foreach (var vendor in Enum.GetValues<SourceSystemType>().Where(v => v != SourceSystemType.Healow))
        {
            VendorScopeCatalog.For(vendor).Should().BeNull($"{vendor} must keep the uniform scope shape");
        }
    }

    [Fact]
    public void EClinicalWorks_wildcard_is_the_short_form_because_system_star_read_does_not_exist()
    {
        VendorScopeCatalog.For(SourceSystemType.Healow)!.WildcardReadAccessLevel.Should().Be("r");
    }
}
