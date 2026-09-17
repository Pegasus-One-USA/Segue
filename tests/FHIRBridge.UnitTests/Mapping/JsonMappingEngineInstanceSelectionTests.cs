using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// RawArrayValues exists so a rule chain led by ConcatenationTemplating/ArrayListOperations can act on every
/// occurrence of a repeating field rather than the single value an ArrayPolicy collapsed it to — that is the only
/// way "join both lines of the address" can work. But it used to carry every occurrence across EVERY instance of
/// the repeating parent, which silently overrode the field's own Instance Selection.
///
/// Epic returns the same name twice (use=official and use=usual), so "Patient.name.given" set to First still fed
/// the concat rule all four given names and wrote "Camila Maria Camila Maria" — while the mapping screen plainly
/// said First. The list must be scoped to the selected instance; "All records" (RepeatParent) stays the way to
/// span them all.
/// </summary>
public sealed class JsonMappingEngineInstanceSelectionTests
{
    // Trimmed from the real Epic payload: two name entries carrying identical given names, one address with two
    // lines. Those two shapes are what separate "values within one instance" from "values across instances".
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "id": "erXuFYUfucBZaryVksYEcMg3",
          "name": [
            { "use": "official", "family": "Lopez", "given": [ "Camila", "Maria" ] },
            { "use": "usual",    "family": "Lopez", "given": [ "Camila", "Maria" ] }
          ],
          "address": [
            { "use": "home", "line": [ "3268 West Johnson St.", "Apt 117" ], "city": "GARLAND" }
          ]
        }
        """;

    private static readonly JsonMappingEngine Sut = new();

    private static MappingFieldDto Field(
        string target, string jsonPath, string[] ancestors, ArrayPolicy policy) => new(
        TargetField: target,
        JsonPath: jsonPath,
        ValueType: MappingValueType.String,
        IsRequired: false,
        DefaultValue: null,
        Format: null,
        ResourceType: "Patient",
        DestinationObject: "dbo.Patient",
        ArrayPolicy: policy,
        ArrayAncestors: ancestors);

    private static IReadOnlyList<object?> RawValuesFor(
        string target, string jsonPath, string[] ancestors, ArrayPolicy policy)
    {
        var result = Sut.Map(PatientJson, [Field(target, jsonPath, ancestors, policy)]);
        return result.RawArrayValues?[target] ?? [];
    }

    [Fact]
    public void First_scopes_a_repeating_primitive_to_the_selected_instance()
    {
        // name[0].given only — name[1] is a duplicate entry Epic sends under use=usual.
        RawValuesFor("GivenName", "$.name[*].given[*]", ["name"], ArrayPolicy.FirstItem)
            .Should().Equal("Camila", "Maria");
    }

    [Fact]
    public void First_still_keeps_every_value_inside_that_one_instance()
    {
        // The case the override exists for: one address, two lines, both needed so concat can join them.
        RawValuesFor("AddressLine1", "$.address[*].line[*]", ["address"], ArrayPolicy.FirstItem)
            .Should().Equal("3268 West Johnson St.", "Apt 117");
    }

    [Fact]
    public void All_records_still_spans_every_instance()
    {
        // RepeatParent is what "All records" resolves to — it must keep seeing all four.
        RawValuesFor("GivenName", "$.name[*].given[*]", ["name"], ArrayPolicy.RepeatParent)
            .Should().Equal("Camila", "Maria", "Camila", "Maria");
    }

    [Fact]
    public void First_on_a_scalar_under_a_repeating_parent_takes_only_that_instance()
    {
        RawValuesFor("FamilyName", "$.name[*].family", ["name"], ArrayPolicy.FirstItem)
            .Should().Equal("Lopez");
    }

    [Fact]
    public void A_field_with_no_repeating_parent_is_untouched()
    {
        RawValuesFor("PatientId", "$.id", [], ArrayPolicy.Scalar)
            .Should().Equal("erXuFYUfucBZaryVksYEcMg3");
    }

    [Fact]
    public void The_collapsed_parent_value_still_honours_FirstItem()
    {
        // Narrowing the raw list must not disturb what actually lands in the column when no rule runs.
        var result = Sut.Map(PatientJson, [Field("GivenName", "$.name[*].given[*]", ["name"], ArrayPolicy.FirstItem)]);

        result.Values["GivenName"].Should().Be("Camila");
    }
}
