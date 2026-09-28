using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// "Match criteria" — picking a repeat by its content rather than its position — was the one instance
/// selection the mapping UI offered but the engine could not express: ResolveAll parsed "*" and integers only,
/// so every criteria mapping silently collapsed to the FIRST instance. A mapping that plainly said
/// use = usual wrote the official name on every record, flagged only by a "preview only" caveat in the UI.
///
/// The wire form is a predicate index token, "name[?use=usual]", which this engine now matches elements with.
/// </summary>
public sealed class JsonMappingEngineMatchCriteriaTests
{
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "name": [
            { "use": "official", "text": "Warren James McGinnis III", "family": "McGinnis" },
            { "use": "usual",    "text": "Warren J McGinnis",         "family": "McGinnis" },
            { "use": "old",      "text": "MCGINNIS,WARREN",           "family": "McGinnis" }
          ],
          "telecom": [
            { "system": "phone", "use": "home",   "value": "555-0100", "rank": 2 },
            { "system": "email", "use": "work",   "value": "w@example.org" }
          ]
        }
        """;

    private static readonly JsonMappingEngine Sut = new();

    private static object? MapOne(string jsonPath, MappingValueType valueType = MappingValueType.String)
    {
        var field = new MappingFieldDto(
            TargetField: "Target",
            JsonPath: jsonPath,
            ValueType: valueType,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Patient",
            DestinationObject: "dbo.Patient",
            ArrayPolicy: valueType == MappingValueType.Json ? ArrayPolicy.StoreJson : ArrayPolicy.FirstItem);

        var result = Sut.Map(PatientJson, [field]);
        return result.Values.TryGetValue("Target", out var value) ? value : null;
    }

    [Fact]
    public void Selects_the_repeat_whose_field_equals_the_value()
    {
        MapOne("$.name[?use=usual].text").Should().Be("Warren J McGinnis");
    }

    /// <summary>The whole-node form: the matching element itself, stored as JSON.</summary>
    [Fact]
    public void Selects_a_whole_node_repeat_by_criteria()
    {
        var json = MapOne("$.name[?use=usual]", MappingValueType.Json)?.ToString();

        // GetRawText() preserves the source document's own spacing, so match on content rather than layout.
        json.Should().Contain("usual").And.Contain("Warren J McGinnis");
        json.Should().NotContain("official", "the other repeats must not come along");
    }

    [Fact]
    public void Matching_is_case_insensitive_on_the_value()
    {
        MapOne("$.name[?use=USUAL].text").Should().Be("Warren J McGinnis");
    }

    [Fact]
    public void Supports_not_equals()
    {
        // FirstItem over everything that is not "official" — the usual name comes first.
        MapOne("$.name[?use!=official].text").Should().Be("Warren J McGinnis");
    }

    [Fact]
    public void Supports_contains()
    {
        MapOne("$.name[?text~MCGINNIS,].text").Should().Be("MCGINNIS,WARREN");
    }

    /// <summary>A non-string value still reads the way someone types it into the criteria box.</summary>
    [Fact]
    public void Matches_a_numeric_field_textually()
    {
        MapOne("$.telecom[?rank=2].value").Should().Be("555-0100");
    }

    [Fact]
    public void A_criteria_matching_nothing_writes_nothing_rather_than_falling_back_to_the_first()
    {
        MapOne("$.name[?use=nickname].text").Should().BeNull();
    }

    /// <summary>An element missing the field entirely is "not equal to" what was asked for — the email
    /// telecom has no rank, so it is what a "rank != 2" selection finds.</summary>
    [Fact]
    public void A_missing_field_counts_as_not_equal()
    {
        MapOne("$.telecom[?rank!=2].value").Should().Be("w@example.org");
    }

    [Fact]
    public void Leaves_an_ordinary_wildcard_and_index_path_alone()
    {
        MapOne("$.name[*].text").Should().Be("Warren James McGinnis III");
        MapOne("$.name[1].text").Should().Be("Warren J McGinnis");
    }
}
