using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class DataQualityGovernanceNodesTests
{
    [Fact]
    public void DefaultNullHandlingNode_coalesces_to_the_first_present_value()
    {
        var result = new DefaultNullHandlingNode().Execute(
            new object?[] { null, "", "home-number" }, new Dictionary<string, string>(), null);
        result.Value.Should().Be("home-number");
    }

    [Fact]
    public void DefaultNullHandlingNode_maps_sentinels_to_null()
    {
        var result = new DefaultNullHandlingNode().Execute("N/A", new Dictionary<string, string>(), null);
        result.Value.Should().BeNull();
    }

    [Fact]
    public void DefaultNullHandlingNode_adds_a_data_absent_reason_marker_when_nothing_present_and_configured()
    {
        var config = new Dictionary<string, string> { ["addDataAbsentReason"] = "true" };
        var result = new DefaultNullHandlingNode().Execute(null, config, null);
        var marker = (System.Text.Json.Nodes.JsonObject)result.Value!;
        marker["_dataAbsentReason"]!.GetValue<string>().Should().Be("unknown");
    }

    [Fact]
    public void DateMathAgeNode_computes_age_at_a_reference_date()
    {
        var config = new Dictionary<string, string> { ["operation"] = "age", ["referenceDate"] = "2026-01-01" };
        var result = new DateMathAgeNode().Execute("1950-02-01", config, null);
        result.Value.Should().Be(75);
    }

    [Fact]
    public void DateMathAgeNode_redacts_ages_over_89_by_default()
    {
        var config = new Dictionary<string, string> { ["operation"] = "age", ["referenceDate"] = "2026-01-01" };
        var result = new DateMathAgeNode().Execute("1930-01-01", config, null);
        result.Value.Should().BeNull();
    }

    [Fact]
    public void DateMathAgeNode_adds_a_full_iso_duration_with_years_and_months()
    {
        var config = new Dictionary<string, string> { ["operation"] = "add", ["duration"] = "P1Y6M" };
        var result = new DateMathAgeNode().Execute("2020-01-01", config, null);
        result.Value.Should().Be("2021-07-01");
    }

    [Fact]
    public void DateMathAgeNode_shift_falls_back_to_fixed_days_without_a_secret_or_patient_id()
    {
        var config = new Dictionary<string, string> { ["operation"] = "shift", ["days"] = "5" };
        var result = new DateMathAgeNode().Execute("2020-01-01", config, secret: null);
        result.Value.Should().Be("2020-01-06");
    }

    [Fact]
    public void DateMathAgeNode_shift_is_deterministic_for_the_same_patient_and_secret()
    {
        var config = new Dictionary<string, string> { ["operation"] = "shift", ["_patientId"] = "patient-1" };
        var first = new DateMathAgeNode().Execute("2020-01-01", config, "vault-secret");
        var second = new DateMathAgeNode().Execute("2020-01-01", config, "vault-secret");
        first.Value.Should().Be(second.Value);
    }

    [Fact]
    public void HashingMaskingNode_produces_a_stable_hash_for_the_same_input_and_key()
    {
        var config = new Dictionary<string, string> { ["mode"] = "hash" };
        var first = new HashingMaskingNode().Execute("A12345", config, "vault-secret");
        var second = new HashingMaskingNode().Execute("A12345", config, "vault-secret");
        first.Value.Should().Be(second.Value);
        first.Value.Should().NotBe("A12345");
    }

    [Fact]
    public void HashingMaskingNode_fails_without_a_secret()
    {
        var config = new Dictionary<string, string> { ["mode"] = "hash" };
        var result = new HashingMaskingNode().Execute("A12345", config, secret: null);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void HashingMaskingNode_masks_keeping_only_the_last_N_characters()
    {
        var config = new Dictionary<string, string> { ["mode"] = "mask", ["keepLength"] = "4" };
        var result = new HashingMaskingNode().Execute("123-45-6789", config, null);
        result.Value.Should().Be("*******6789");
    }
}
