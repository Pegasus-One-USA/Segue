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

/// <summary>
/// A String column must never receive an array's raw JSON text. ConvertElement fell through to
/// element.ToString() whenever the format was not "directField", and unconditionally whenever
/// DeferTypeToTransform was set (which a rule declaring an ExpectedValueType turns on) — so a path resolving to
/// the array itself, e.g. "$.name[*].given", wrote ["**mila","*aria"] into an nvarchar column, brackets and
/// quotes included.
///
/// The second effect was worse than the cosmetics: the array collapses to ONE value, so a
/// ConcatenationTemplating rule — the very reason the type was deferred — received a single string instead of
/// the items and passed it straight through. Joining is what lets that rule see the values at all.
/// </summary>
public sealed class JsonMappingEngineArrayTextTests
{
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "name": [ { "family": "Lopez", "given": [ "Camila", "Maria" ] } ]
        }
        """;

    private static readonly JsonMappingEngine Sut = new();

    private static MappingFieldDto Field(string? format, bool deferTypeToTransform) => new(
        TargetField: "GivenName",
        JsonPath: "$.name[*].given",
        ValueType: MappingValueType.String,
        IsRequired: false,
        DefaultValue: null,
        Format: format,
        ResourceType: "Patient",
        DestinationObject: "dbo.Patient",
        ArrayPolicy: ArrayPolicy.FirstItem,
        ArrayAncestors: ["name"],
        DeferTypeToTransform: deferTypeToTransform);

    private static object? Map(string? format, bool defer) =>
        Sut.Map(PatientJson, [Field(format, defer)]).Values["GivenName"];

    [Theory]
    [InlineData("directField", false)]
    [InlineData("directField", true)]   // a rule declaring an ExpectedValueType sets this
    [InlineData(null, false)]
    [InlineData("wholeNodeAsJson", false)]
    public void An_array_never_reaches_a_string_column_as_json_text(string? format, bool defer)
    {
        var value = Map(format, defer)?.ToString();

        value.Should().NotBeNull();
        value.Should().NotStartWith("[", "a text column holds values, not a JSON document");
        value.Should().NotContain("\"", "quotes are JSON syntax, not part of the name");
        value.Should().Contain("Camila").And.Contain("Maria");
    }

    [Fact]
    public void A_deferred_array_is_handed_to_the_rule_as_joined_values()
    {
        // ValueType Json is the declared way to ask for JSON, and is unaffected.
        Map("directField", defer: true).Should().Be("Camila, Maria");
    }

    [Fact]
    public void A_json_typed_field_still_receives_the_raw_document()
    {
        var jsonField = Field("wholeNodeAsJson", deferTypeToTransform: false) with { ValueType = MappingValueType.Json };

        var value = Sut.Map(PatientJson, [jsonField]).Values["GivenName"]?.ToString();

        value.Should().Be("""[ "Camila", "Maria" ]""", "ValueType Json is the explicit way to ask for a document");
    }
}

/// <summary>
/// JoinArrayOfStrings keeps scalars and DROPS objects and nested arrays. Routing every array through it — which
/// is what removing the old directField guard did — turned "telecom":[{...},{...}] into an empty string: every
/// item dropped, column blank, run green. Silent data loss is worse than the bracketed JSON the join exists to
/// avoid, so only a scalar array may be joined.
/// </summary>
public sealed class JsonMappingEngineObjectArrayTests
{
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "telecom": [
            { "system": "phone", "value": "+1 469-888-8888", "use": "work" },
            { "system": "email", "value": "knixontestemail2@epic.com" }
          ],
          "given": [ "Camila", "Maria" ],
          "mixed": [ "Camila", { "text": "Camila Maria" } ]
        }
        """;

    private static readonly JsonMappingEngine Sut = new();

    private static MappingFieldDto Field(string path, bool defer, MappingValueType type = MappingValueType.String) => new(
        TargetField: "Col", JsonPath: path, ValueType: type,
        IsRequired: false, DefaultValue: null, Format: "directField",
        ResourceType: "Patient", DestinationObject: "dbo.Patient",
        ArrayPolicy: ArrayPolicy.FirstItem, ArrayAncestors: [], DeferTypeToTransform: defer);

    private static string? Map(string path, bool defer = false, MappingValueType type = MappingValueType.String) =>
        Sut.Map(PatientJson, [Field(path, defer, type)]).Values["Col"]?.ToString();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // the deferred path had the same defect
    public void An_array_of_objects_is_never_silently_emptied(bool defer)
    {
        var value = Map("$.telecom", defer);

        value.Should().NotBeNullOrEmpty("dropping every item writes a blank column while the run reports success");
        value.Should().Contain("469-888-8888", "the phone number is the data this column exists to carry");
        value.Should().Contain("knixontestemail2@epic.com");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_mixed_array_keeps_its_object_items_too(bool defer)
    {
        // "Camila" alone would be a silent half-loss — the object item carries data as well.
        Map("$.mixed", defer).Should().Contain("Camila Maria");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_scalar_array_still_joins_without_json_punctuation(bool defer)
    {
        var value = Map("$.given", defer);

        value.Should().Be("Camila, Maria");
        value.Should().NotStartWith("[").And.NotContain("\"");
    }

    [Fact]
    public void A_json_typed_column_still_receives_the_document_verbatim()
    {
        Map("$.telecom", defer: false, type: MappingValueType.Json)
            .Should().StartWith("[", "ValueType Json is the explicit way to ask for a document");
    }
}
