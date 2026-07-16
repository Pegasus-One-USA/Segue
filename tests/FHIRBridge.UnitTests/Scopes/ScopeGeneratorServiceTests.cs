using FHIRBridge.Application.Services;
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
}
