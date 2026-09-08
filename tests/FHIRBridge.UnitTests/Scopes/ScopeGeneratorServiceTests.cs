using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Scopes;

/// <summary>
/// Pins the SMART scope-generation rules: prefix by application type, suffix by version, interactive base scopes,
/// and validation against a source's advertised scopes_supported (exact + wildcard).
/// </summary>
public sealed class ScopeGeneratorServiceTests
{
    private readonly ScopeGeneratorService _sut = new();

    [Fact]
    public void Ehr_launch_v2_uses_user_prefix_rs_suffix_and_launch_scope()
    {
        var result = _sut.Generate(ApplicationType.EhrLaunch, ["Patient", "Observation"], "v2", true, null);

        result.ScopeString.Should().Be("openid fhirUser offline_access launch user/Observation.rs user/Patient.rs");
        result.ValidatedAgainstDiscovery.Should().BeFalse();
    }

    [Fact]
    public void Standalone_v1_uses_read_suffix_and_no_launch_scope()
    {
        var result = _sut.Generate(ApplicationType.Standalone, ["Patient"], "v1", false, null);

        result.Scopes.Should().Contain("user/Patient.read");
        result.Scopes.Should().NotContain("launch").And.NotContain("launch/patient");
    }

    [Fact]
    public void Patient_uses_patient_prefix()
    {
        var result = _sut.Generate(ApplicationType.Patient, ["Observation"], "v2", true, null);

        result.Scopes.Should().Contain("patient/Observation.rs").And.Contain("launch/patient");
    }

    [Fact]
    public void Backend_uses_system_prefix_and_no_interactive_scopes()
    {
        var result = _sut.Generate(ApplicationType.Backend, ["Patient"], "v2", true, null);

        result.Scopes.Should().Equal("system/Patient.rs");
        result.Scopes.Should().NotContain("openid").And.NotContain("launch");
    }

    [Fact]
    public void Backend_group_export_adds_group_read_scope_ecw_v1()
    {
        // eCW bulk (Group/{id}/$export) needs system/Group.read on top of the per-resource scopes. Regression guard:
        // Group is otherwise excluded from the per-resource set, so without isGroupExport the token misses Group.read
        // and eCW's authorization rule denies the export (HTTP 403 "HAPI-0333: Access denied by rule").
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient", "AllergyIntolerance"], "v1", false, null,
            vendor: SourceSystemType.Healow, isGroupExport: true);

        result.Scopes.Should().Contain("system/Group.read");
        result.Scopes.Should().Contain("system/Patient.read");
    }

    [Fact]
    public void Backend_group_export_uses_version_suffix_for_group_v2()
    {
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient"], "v2", true, null, isGroupExport: true);

        result.Scopes.Should().Contain("system/Group.rs");
    }

    [Fact]
    public void Backend_single_patient_does_not_add_group_scope()
    {
        // Default (isGroupExport: false) must NOT request Group — eCW requires it excluded from Backend Single Patient.
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient", "AllergyIntolerance"], "v1", false, null,
            vendor: SourceSystemType.Healow);

        result.Scopes.Should().NotContain(s => s.StartsWith("system/Group", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Interactive_ignores_group_export_flag()
    {
        // Group export is a system/ (Backend) concept — an interactive prefix must never gain a Group scope.
        var result = _sut.Generate(
            ApplicationType.EhrLaunch, ["Patient"], "v2", true, null, isGroupExport: true);

        result.Scopes.Should().NotContain(s => s.Contains("Group", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flags_scopes_not_advertised_by_discovery()
    {
        // Server advertises only Patient (exact) — Observation should be reported unsupported.
        var result = _sut.Generate(
            ApplicationType.EhrLaunch, ["Patient", "Observation"], "v2", true,
            ["user/Patient.rs", "openid", "fhirUser", "launch", "offline_access"]);

        result.ValidatedAgainstDiscovery.Should().BeTrue();
        result.UnsupportedScopes.Should().ContainSingle().Which.Should().Be("user/Observation.rs");
    }

    [Fact]
    public void Wildcard_advertised_scope_covers_concrete_resource()
    {
        var result = _sut.Generate(
            ApplicationType.EhrLaunch, ["Patient", "Observation"], "v2", true,
            ["user/*.rs"]);

        result.UnsupportedScopes.Should().BeEmpty();
    }

    // ── Discovery-derived access levels (Tier 1) ──────────────────────────────────────────────────────────────

    [Fact]
    public void System_scopes_take_the_access_level_the_server_actually_advertises()
    {
        // The caller asks for v2, but this server publishes ServiceRequest as '.r' and Patient as '.read' — the
        // eCW shape. Honouring the advertised spelling per resource is the whole point: a vendor that fails the
        // WHOLE token request on one unrecognized scope makes a single wrong suffix cost every other scope too.
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient", "ServiceRequest"], "v2", true,
            ["system/Patient.read", "system/ServiceRequest.r"]);

        result.Scopes.Should().BeEquivalentTo(["system/Patient.read", "system/ServiceRequest.r"]);
        result.UnsupportedScopes.Should().BeEmpty();
    }

    [Fact]
    public void Discovery_derived_level_wins_over_a_stale_vendor_profile()
    {
        // VendorScopeCatalog's eCW profile (captured from one practice on one day) spells Coverage '.r'. A tenant
        // that advertises '.read' must get '.read' — the live document outranks the snapshot, which is what stops
        // the captured map going stale.
        var result = _sut.Generate(
            ApplicationType.Backend, ["Coverage"], "v1", true,
            ["system/Coverage.read"], SourceSystemType.Healow);

        result.Scopes.Should().ContainSingle().Which.Should().Be("system/Coverage.read");
    }

    [Fact]
    public void Write_only_advertised_levels_never_substitute_for_a_read_scope()
    {
        // FHIRBridge reads. A server advertising only create/update/delete for a type publishes no read scope, so
        // the type is reported rather than requested — asking for write access it doesn't need would be wrong.
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient"], "v1", true,
            ["system/Patient.c", "system/Patient.u"], SourceSystemType.Healow);

        result.Scopes.Should().NotContain("system/Patient.c").And.NotContain("system/Patient.u");
        result.UnsupportedScopes.Should().ContainSingle().Which.Should().Be("system/Patient.read");
    }

    [Fact]
    public void Unprofiled_vendor_falls_back_to_the_uniform_suffix_when_discovery_is_silent()
    {
        // Epic has no profile: absence from a possibly-unreachable discovery document is not authority to drop a
        // resource, so it keeps the uniform suffix and is merely flagged.
        var result = _sut.Generate(
            ApplicationType.Backend, ["Patient"], "v2", true,
            ["system/Observation.rs"]);

        result.Scopes.Should().ContainSingle().Which.Should().Be("system/Patient.rs");
        result.UnsupportedScopes.Should().ContainSingle().Which.Should().Be("system/Patient.rs");
    }

    // ── Vendor policy exclusions ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Group_is_never_requested_for_eCW_even_when_advertised()
    {
        // eCW publishes system/Group.read, but its Backend Authentication guide requires that scope EXCLUDED from
        // Backend Single Patient calls — the only eCW backend flow FHIRBridge supports. Policy, not vocabulary:
        // the discovery tier would otherwise hand it straight back and break single-patient auth.
        var result = _sut.Generate(
            ApplicationType.Backend, ["Group", "Patient"], "v1", true,
            ["system/Group.read", "system/Patient.read"], SourceSystemType.Healow);

        result.Scopes.Should().ContainSingle().Which.Should().Be("system/Patient.read");
        result.UnsupportedScopes.Should().ContainSingle().Which.Should().Be("system/Group.read");
    }

    [Fact]
    public void Group_is_still_requested_for_a_vendor_with_no_such_rule()
    {
        var result = _sut.Generate(
            ApplicationType.Backend, ["Group"], "v1", true,
            ["system/Group.read"]);

        result.Scopes.Should().ContainSingle().Which.Should().Be("system/Group.read");
    }

    [Fact]
    public void Interactive_prefixes_are_untouched_by_the_discovery_tier()
    {
        // Tier 1 is scoped to system/ deliberately, exactly like the vendor profile — an interactive audience's
        // generation stays byte-identical, so this change cannot regress EHR launch / standalone / patient.
        var result = _sut.Generate(
            ApplicationType.EhrLaunch, ["Patient"], "v2", true,
            ["user/Patient.read"]);

        result.Scopes.Should().Contain("user/Patient.rs");
    }
}
