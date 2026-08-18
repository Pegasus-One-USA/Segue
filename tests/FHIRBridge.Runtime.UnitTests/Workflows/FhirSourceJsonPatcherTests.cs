using System.Text.Json.Nodes;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

public sealed class FhirSourceJsonPatcherTests
{
    [Fact]
    public void Replaces_a_top_level_object_with_a_structure_building_nodes_output()
    {
        const string sourceJson = """{"resourceType":"Condition","id":"1","code":{"coding":[{"system":"http://old","code":"OLD"}]}}""";
        var enrichedCode = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject { ["system"] = "http://hl7.org/fhir/sid/icd-10-cm", ["code"] = "E11.9", ["display"] = "Type 2 diabetes" }),
            ["text"] = "Type 2 diabetes",
        };

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("code", enrichedCode)]);

        var root = JsonNode.Parse(patched!)!.AsObject();
        root["code"]!["text"]!.GetValue<string>().Should().Be("Type 2 diabetes");
        root["code"]!["coding"]![0]!["display"]!.GetValue<string>().Should().Be("Type 2 diabetes");
        root["id"]!.GetValue<string>().Should().Be("1", "unrelated fields must survive the patch untouched");
    }

    [Fact]
    public void Creates_missing_intermediate_objects_and_arrays()
    {
        const string sourceJson = """{"resourceType":"Observation","id":"1"}""";

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("component[0].valueQuantity", 98.6)]);

        var root = JsonNode.Parse(patched!)!.AsObject();
        root["component"]![0]!["valueQuantity"]!.GetValue<double>().Should().Be(98.6);
    }

    [Fact]
    public void Writes_a_scalar_leaf_value()
    {
        const string sourceJson = """{"birthDate":"1990-01-01"}""";

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("birthDate", "1990-01-02")]);

        JsonNode.Parse(patched!)!["birthDate"]!.GetValue<string>().Should().Be("1990-01-02");
    }

    [Fact]
    public void Returns_the_original_json_unchanged_when_there_are_no_patches()
    {
        const string sourceJson = """{"a":1}""";
        FhirSourceJsonPatcher.ApplyPatches(sourceJson, []).Should().Be(sourceJson);
    }

    [Fact]
    public void Returns_the_original_input_when_it_is_null_or_not_an_object()
    {
        FhirSourceJsonPatcher.ApplyPatches(null, [("code", "x")]).Should().BeNull();
        FhirSourceJsonPatcher.ApplyPatches("not json", [("code", "x")]).Should().Be("not json");
    }

    [Fact]
    public void TryReadSiblingDisplay_reads_the_display_next_to_a_code_leaf_through_an_array()
    {
        const string sourceJson = """
            {"code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"J18.1","display":"Lobar pneumonia, unspecified organism"}]}}
            """;

        FhirSourceJsonPatcher.TryReadSiblingDisplay(sourceJson, "code.coding.code")
            .Should().Be("Lobar pneumonia, unspecified organism");
    }

    [Fact]
    public void TryReadSiblingDisplay_returns_null_when_the_path_does_not_end_in_code()
    {
        const string sourceJson = """{"birthDate":"1990-01-01"}""";
        FhirSourceJsonPatcher.TryReadSiblingDisplay(sourceJson, "birthDate").Should().BeNull();
    }

    [Fact]
    public void TryReadSiblingDisplay_returns_null_when_no_display_is_present()
    {
        const string sourceJson = """{"code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"J18.1"}]}}""";
        FhirSourceJsonPatcher.TryReadSiblingDisplay(sourceJson, "code.coding.code").Should().BeNull();
    }

    [Fact]
    public void TryReadSiblingDisplay_returns_null_for_null_or_blank_input()
    {
        FhirSourceJsonPatcher.TryReadSiblingDisplay(null, "code.coding.code").Should().BeNull();
        FhirSourceJsonPatcher.TryReadSiblingDisplay("""{"code":{}}""", null).Should().BeNull();
    }
}
