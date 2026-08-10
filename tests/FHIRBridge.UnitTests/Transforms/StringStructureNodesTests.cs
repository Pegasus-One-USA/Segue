using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class StringStructureNodesTests
{
    [Fact]
    public void StringNormalizationNode_trims_collapses_whitespace_and_title_cases()
    {
        var config = new Dictionary<string, string> { ["case"] = "title" };
        new StringNormalizationNode().Execute("  john   doe  ", config, null).Value.Should().Be("John Doe");
    }

    [Fact]
    public void StringNormalizationNode_converts_empty_result_to_null()
    {
        var config = new Dictionary<string, string> { ["maxLength"] = "0" };
        new StringNormalizationNode().Execute("   ", config, null).Value.Should().BeNull();
    }

    [Fact]
    public void StringNormalizationNode_applies_a_valid_regex_replace()
    {
        var config = new Dictionary<string, string> { ["regexPattern"] = @"\d+", ["regexReplacement"] = "#" };
        new StringNormalizationNode().Execute("abc123def456", config, null).Value.Should().Be("abc#def#");
    }

    [Fact]
    public void StringNormalizationNode_fails_cleanly_on_an_invalid_regex_instead_of_throwing()
    {
        var config = new Dictionary<string, string> { ["regexPattern"] = "(unclosed" };
        var result = new StringNormalizationNode().Execute("anything", config, null);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void StringNormalizationNode_strips_diacritics_by_default()
    {
        new StringNormalizationNode().Execute("jóhn", new Dictionary<string, string>(), null).Value.Should().Be("john");
    }

    [Fact]
    public void ConcatenationTemplatingNode_fills_a_positional_template()
    {
        var config = new Dictionary<string, string> { ["template"] = "Dr. {0} {1}" };
        var result = new ConcatenationTemplatingNode().Execute(new object?[] { "Jane", "Roe" }, config, null);
        result.Value.Should().Be("Dr. Jane Roe");
    }

    [Fact]
    public void ConcatenationTemplatingNode_splits_using_a_regex_delimiter_when_configured()
    {
        var config = new Dictionary<string, string> { ["mode"] = "split", ["splitDelimiter"] = @"\s*,\s*", ["splitIsRegex"] = "true" };
        var result = new ConcatenationTemplatingNode().Execute("A, B,C", config, null);
        result.Value.Should().BeEquivalentTo(new[] { "A", "B", "C" });
    }

    [Fact]
    public void ConcatenationTemplatingNode_joins_non_null_parts_with_a_separator()
    {
        var result = new ConcatenationTemplatingNode().Execute(
            new object?[] { "Jane", "Roe" }, new Dictionary<string, string> { ["separator"] = " " }, null);
        result.Value.Should().Be("Jane Roe");
    }

    [Fact]
    public void ConcatenationTemplatingNode_splits_a_delimited_string_into_an_array()
    {
        var config = new Dictionary<string, string> { ["mode"] = "split", ["splitDelimiter"] = "|" };
        var result = new ConcatenationTemplatingNode().Execute("A|B|C", config, null);
        result.Value.Should().BeEquivalentTo(new[] { "A", "B", "C" });
    }

    [Fact]
    public void ArrayListOperationsNode_selects_the_first_item()
    {
        var result = new ArrayListOperationsNode().Execute(
            new object?[] { "mrn-value", "npi-value" }, new Dictionary<string, string> { ["operation"] = "first" }, null);
        result.Value.Should().Be("mrn-value");
    }

    [Fact]
    public void ArrayListOperationsNode_dedupes_by_string_value()
    {
        var result = new ArrayListOperationsNode().Execute(
            new object?[] { "a", "a", "b" }, new Dictionary<string, string> { ["operation"] = "dedupe" }, null);
        result.Value.Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void ArrayListOperationsNode_filters_by_direct_value_equality()
    {
        var config = new Dictionary<string, string> { ["operation"] = "filter", ["predicateValue"] = "MRN" };
        var result = new ArrayListOperationsNode().Execute(new object?[] { "MRN", "NPI" }, config, null);
        result.Value.Should().BeEquivalentTo(new[] { "MRN" });
    }

    [Fact]
    public void ArrayListOperationsNode_filters_json_objects_by_a_predicate_field()
    {
        var mrn = new System.Text.Json.Nodes.JsonObject { ["system"] = "MRN", ["value"] = "123" };
        var npi = new System.Text.Json.Nodes.JsonObject { ["system"] = "NPI", ["value"] = "456" };
        var config = new Dictionary<string, string> { ["operation"] = "filter", ["predicateField"] = "system", ["predicateValue"] = "MRN" };
        var result = new ArrayListOperationsNode().Execute(new object?[] { mrn, npi }, config, null);
        var filtered = ((object?[])result.Value!).Cast<System.Text.Json.Nodes.JsonObject>().ToList();
        filtered.Should().HaveCount(1);
        filtered[0]["value"]!.GetValue<string>().Should().Be("123");
    }

    [Fact]
    public void ArrayListOperationsNode_flattens_one_level_of_nested_arrays()
    {
        var config = new Dictionary<string, string> { ["operation"] = "flatten" };
        var result = new ArrayListOperationsNode().Execute(new object?[] { new object?[] { "a", "b" }, "c" }, config, null);
        result.Value.Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }
}
