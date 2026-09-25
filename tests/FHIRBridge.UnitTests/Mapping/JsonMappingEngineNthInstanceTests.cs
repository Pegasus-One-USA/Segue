using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// "Nth instance" (field-mapping-model.ts's instance.type === 'nth') stamps a "index=N" marker onto
/// MappingFieldDto.Format — the counterpart to "aggregate=csv" (see HasCsvAggregate) — which
/// JsonMappingEngine.SelectInstance reads to narrow the resolved occurrence list down to just the (0-based)
/// N-th DISTINCT instance of the repeating parent, before ArrayPolicy (always FirstItem for this selection)
/// ever runs. 0 means the first occurrence, matching plain "First" (no marker at all) exactly.
/// </summary>
public sealed class JsonMappingEngineNthInstanceTests
{
    // The exact shape from the reported case: four telecom entries, only three of which carry "use" — the
    // fourth (email) has none, so it must never count as an "instance" of the .use field at all.
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "telecom": [
            { "system": "phone", "value": "897-532-5871", "use": "home" },
            { "system": "phone", "value": "+1(585)-998-7425", "use": "mobile" },
            { "system": "phone", "value": "987-452-2522", "use": "work" },
            { "system": "email", "value": "test@test.com" }
          ],
          "name": [
            { "use": "official", "family": "Lopez", "given": [ "Camila", "Maria" ] },
            { "use": "usual",    "family": "Lopez", "given": [ "Camila", "Maria" ] }
          ]
        }
        """;

    private static readonly JsonMappingEngine Sut = new();

    private static MappingFieldDto Field(
        string target, string jsonPath, string[] ancestors, int index,
        bool isRequired = false, string? defaultValue = null) => new(
        TargetField: target,
        JsonPath: jsonPath,
        ValueType: MappingValueType.String,
        IsRequired: isRequired,
        DefaultValue: defaultValue,
        Format: $"directField;index={index}",
        ResourceType: "Patient",
        DestinationObject: "dbo.Patient",
        ArrayPolicy: ArrayPolicy.FirstItem,
        ArrayAncestors: ancestors);

    [Fact]
    public void Index_1_selects_the_second_telecom_entrys_use_not_the_first()
    {
        var result = Sut.Map(PatientJson, [Field("Use", "$.telecom[*].use", ["telecom"], index: 1)]);

        result.Values["Use"].Should().Be("mobile");
    }

    [Fact]
    public void Index_0_matches_plain_First_selection_exactly()
    {
        var result = Sut.Map(PatientJson, [Field("Use", "$.telecom[*].use", ["telecom"], index: 0)]);

        result.Values["Use"].Should().Be("home");
    }

    [Fact]
    public void Index_2_selects_the_third_telecom_entry()
    {
        var result = Sut.Map(PatientJson, [Field("Use", "$.telecom[*].use", ["telecom"], index: 2)]);

        result.Values["Use"].Should().Be("work");
    }

    [Fact]
    public void Selection_is_by_distinct_outer_instance_not_a_flattened_position_across_nested_arrays()
    {
        // Flattened resolution order for $.name[*].given[*] is [name0.given0="Camila", name0.given1="Maria",
        // name1.given0="Camila", name1.given1="Maria"] — a naive "take resolved[1]" would wrongly return
        // "Maria" (still name[0]'s second given). Instance selection means name[1] as a WHOLE, so index=1 then
        // FirstItem must land on name[1]'s own FIRST given, "Camila", not the flattened element at position 1.
        var result = Sut.Map(PatientJson, [Field("GivenName", "$.name[*].given[*]", ["name"], index: 1)]);

        result.Values["GivenName"].Should().Be("Camila");
    }

    [Fact]
    public void Out_of_range_instance_falls_back_to_the_fields_default_value()
    {
        // Only 4 telecom entries (3 with "use") exist — instance #10 doesn't. Treated exactly like a
        // genuinely absent optional sub-field: the DefaultValue fallback still applies.
        var result = Sut.Map(PatientJson, [Field("Use", "$.telecom[*].use", ["telecom"], index: 10, defaultValue: "unknown")]);

        result.Values["Use"].Should().Be("unknown");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Out_of_range_instance_with_no_default_and_required_records_an_error_and_writes_null()
    {
        var result = Sut.Map(PatientJson, [Field("Use", "$.telecom[*].use", ["telecom"], index: 10, isRequired: true)]);

        result.Values["Use"].Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Contains("Use") && e.Contains("$.telecom[*].use"));
    }

    [Fact]
    public void Out_of_range_instance_with_no_default_and_optional_is_silently_null()
    {
        var result = Sut.Map(PatientJson, [Field("Use", "$.telecom[*].use", ["telecom"], index: 10)]);

        result.Values["Use"].Should().BeNull();
        result.Errors.Should().BeEmpty();
    }
}
