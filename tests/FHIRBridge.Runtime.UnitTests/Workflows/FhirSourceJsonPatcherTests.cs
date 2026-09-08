using System.Text.Json.Nodes;
using FHIRBridge.Application.Services.Transforms;
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

    // ── Repeating elements ──────────────────────────────────────────────────────────────────────────
    // FHIR cardinality is structural: Patient.name is 0..*, so it is an ARRAY whether it holds one entry
    // or five. A write that replaces the array with the object it contains produces a resource a real
    // server rejects — Aidbox answers 422 "Patient.name / The property must be an array". These cover the
    // reported bug (a StringNormalization rule on Patient.name.family with an empty write-back path) and
    // its sibling on a repeating primitive.

    [Fact]
    public void Writing_through_an_unindexed_array_segment_keeps_the_array()
    {
        const string sourceJson = """{"resourceType":"Patient","name":[{"family":"smith","given":["john"]}]}""";

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("name.family", "SMITH")]);

        var name = JsonNode.Parse(patched!)!["name"];
        name.Should().BeOfType<JsonArray>("Patient.name is 0..* — replacing it with an object is a 422");
        name!.AsArray()[0]!["family"]!.GetValue<string>().Should().Be("SMITH");
        // Everything else in that element survives the write.
        name.AsArray()[0]!["given"]!.AsArray()[0]!.GetValue<string>().Should().Be("john");
    }

    [Fact]
    public void Writing_to_an_unindexed_array_leaf_replaces_its_first_item_not_the_array()
    {
        // The reader takes the first element of a repeating primitive, so the writer must put the result
        // back into that same slot — assigning the bare value would collapse given: ["john","q"] to a string.
        const string sourceJson = """{"resourceType":"Patient","name":[{"given":["john","q"]}]}""";

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("name.given", "JOHN")]);

        var given = JsonNode.Parse(patched!)!["name"]!.AsArray()[0]!["given"];
        given.Should().BeOfType<JsonArray>();
        given!.AsArray().Count.Should().Be(2, "the other entries are not this rule's to remove");
        given.AsArray()[0]!.GetValue<string>().Should().Be("JOHN");
        given.AsArray()[1]!.GetValue<string>().Should().Be("q");
    }

    [Fact]
    public void Writing_through_an_empty_array_creates_the_element_rather_than_replacing_the_array()
    {
        const string sourceJson = """{"resourceType":"Patient","name":[]}""";

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("name.family", "SMITH")]);

        var name = JsonNode.Parse(patched!)!["name"];
        name.Should().BeOfType<JsonArray>();
        name!.AsArray()[0]!["family"]!.GetValue<string>().Should().Be("SMITH");
    }

    [Fact]
    public void An_explicit_index_still_targets_that_exact_element()
    {
        const string sourceJson = """{"resourceType":"Patient","name":[{"family":"a"},{"family":"b"}]}""";

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("name[1].family", "B")]);

        var name = JsonNode.Parse(patched!)!["name"]!.AsArray();
        name[0]!["family"]!.GetValue<string>().Should().Be("a");
        name[1]!["family"]!.GetValue<string>().Should().Be("B");
    }

    [Fact]
    public void The_reader_and_writer_agree_on_which_element_an_unindexed_path_means()
    {
        // The invariant behind all of the above: whatever FhirJsonPathReader read is the node the patch
        // lands on. If these two ever disagree, a rule silently transforms one value and writes another.
        const string sourceJson = """{"resourceType":"Patient","name":[{"family":"smith"},{"family":"jones"}]}""";

        FhirJsonPathReader.Read(sourceJson, "name.family").Should().Be("smith");

        var patched = FhirSourceJsonPatcher.ApplyPatches(sourceJson, [("name.family", "SMITH")]);

        FhirJsonPathReader.Read(patched, "name.family").Should().Be("SMITH");
        JsonNode.Parse(patched!)!["name"]!.AsArray()[1]!["family"]!.GetValue<string>()
            .Should().Be("jones", "only the element the reader saw is rewritten");
    }
}
