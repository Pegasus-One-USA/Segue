using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class CodesTerminologyNodesTests
{
    [Fact]
    public void ValueCodeMappingNode_translates_via_the_configured_map()
    {
        var config = new Dictionary<string, string> { ["map"] = """{"M":"male","F":"female"}""" };
        new ValueCodeMappingNode().Execute("M", config, null).Value.Should().Be("male");
    }

    [Fact]
    public void ValueCodeMappingNode_returns_null_for_unmatched_by_default()
    {
        var config = new Dictionary<string, string> { ["map"] = """{"M":"male"}""" };
        new ValueCodeMappingNode().Execute("X", config, null).Value.Should().BeNull();
    }

    [Fact]
    public void CodeableConceptBuilderNode_resolves_a_short_system_key_to_its_canonical_uri()
    {
        var config = new Dictionary<string, string> { ["system"] = "LOINC", ["display"] = "Glucose" };
        var result = new CodeableConceptBuilderNode().Execute("2339-0", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        var coding = (System.Text.Json.Nodes.JsonObject)concept["coding"]![0]!;
        coding["system"]!.GetValue<string>().Should().Be("http://loinc.org");
        coding["code"]!.GetValue<string>().Should().Be("2339-0");
        concept["text"]!.GetValue<string>().Should().Be("Glucose");
    }

    [Fact]
    public void StatusEnumCoercionNode_never_emits_an_out_of_value_set_code()
    {
        var config = new Dictionary<string, string>
        {
            ["map"] = """{"COMPLETE":"final"}""",
            ["fallback"] = "unknown"
        };
        new StatusEnumCoercionNode().Execute("weird-status", config, null).Value.Should().Be("unknown");
        new StatusEnumCoercionNode().Execute("COMPLETE", config, null).Value.Should().Be("final");
    }
}
