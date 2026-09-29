using System.Text.Json.Nodes;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// PerItem only ever fanned out over a real IEnumerable, and a whole-node mapping's value never is one: it
/// arrives as the raw JSON text ArrayPolicy.StoreJson wrote. So a rule on a repeating element was handed
/// "[{...},{...}]" as one opaque string and every scalar node parsed the JSON source as if it were a value.
/// </summary>
public sealed class TransformNodeApplierTests
{
    private const string NameArrayJson =
        """
        [{"use":"official","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]},
         {"use":"usual","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]}]
        """;

    [Fact]
    public void PerItem_runs_the_node_once_per_element_of_a_JSON_array_string()
    {
        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new HumanNameParsingNode(), NameArrayJson,
            new Dictionary<string, string> { ["pattern"] = "FirstLast" }, null, TransformArrayMode.PerItem);

        result.Success.Should().BeTrue();
        // One rendered name per element — an element states its own parts, so FirstLast composes the first
        // given name with the family name rather than parsing anything out of the display string.
        ((object?[])result.Value!).Should().Equal("Warren McGinnis", "Warren McGinnis");
    }

    /// <summary>
    /// The third name on the real record renders from its own parts too, so it keeps the source's correct
    /// casing — its display string is the upper-case comma form "MCGINNIS,WARREN", which is not what reaches
    /// the column.
    /// </summary>
    [Fact]
    public void PerItem_renders_an_element_from_its_parts_not_its_display_string()
    {
        const string json =
            """[{"use":"old","text":"MCGINNIS,WARREN","family":"McGinnis","given":["Warren"]}]""";

        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new HumanNameParsingNode(), json,
            new Dictionary<string, string> { ["pattern"] = "FirstLast" }, null, TransformArrayMode.PerItem);

        ((object?[])result.Value!).Should().Equal("Warren McGinnis");
    }

    /// <summary>An explicit output format still wins over the mode's own order.</summary>
    [Fact]
    public void PerItem_composes_one_display_string_per_element_when_a_format_is_set()
    {
        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new HumanNameParsingNode(), NameArrayJson,
            new Dictionary<string, string> { ["format"] = "First Last Suffix" }, null, TransformArrayMode.PerItem);

        ((object?[])result.Value!).Should().Equal("Warren McGinnis III", "Warren McGinnis III");
    }

    /// <summary>
    /// The guard is the shared layer's, not any one node's: every node that converts a single scalar runs per
    /// element over a JSON array even under Whole — a rule saved as Whole before the mode was chosen per row
    /// must not start failing (and NULLing its column under NullOut) — and never parses the array's JSON text.
    /// </summary>
    [Fact]
    public void Whole_fans_out_an_array_for_any_node_that_converts_a_single_value()
    {
        ITransformNode[] scalarNodes =
            [new HumanNameParsingNode(), new AddressParsingNode(), new StringNormalizationNode(), new DateTimeFormatNode()];

        foreach (var node in scalarNodes)
        {
            var whole = TransformNodeApplier.ExecuteWithArrayMode(
                node, NameArrayJson, new Dictionary<string, string>(), null, TransformArrayMode.Whole);
            var perItem = TransformNodeApplier.ExecuteWithArrayMode(
                node, NameArrayJson, new Dictionary<string, string>(), null, TransformArrayMode.PerItem);

            whole.Success.Should().Be(perItem.Success, $"{node.NodeType} converts one value, so it runs per element");
            if (whole.Success)
            {
                ((object?[])whole.Value!).Should().HaveCount(2);
                ((object?[])whole.Value!).Select(v => v?.ToString()).Should().Equal(
                    ((object?[])perItem.Value!).Select(v => v?.ToString()));
            }
        }
    }

    /// <summary>A structured element is one value, not a collection of its key/value pairs.</summary>
    [Fact]
    public void PerItem_does_not_fan_out_over_a_single_element()
    {
        var element = JsonNode.Parse("""{"family":"Roe","given":["Jane"]}""")!;

        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new HumanNameParsingNode(), element,
            new Dictionary<string, string> { ["format"] = "First Last" }, null, TransformArrayMode.PerItem);

        result.Value.Should().Be("Jane Roe");
    }

    [Fact]
    public void Whole_still_hands_an_array_to_a_node_that_declares_it_reads_collections()
    {
        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new ArrayListOperationsNode(), """["roe","doe"]""",
            new Dictionary<string, string> { ["operation"] = "count" }, null, TransformArrayMode.Whole);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(2);
    }

    /// <summary>
    /// The reported address case. A mapping of the whole Address element handed AddressParsingNode the
    /// element's raw JSON, which it split on the commas inside that JSON — writing `"country":"US"` into the
    /// city column and `"period":{"start":…}}` into state. Every node but HumanNameParsing reads a scalar, so
    /// the shared layer reduces an element to its own `text` first: exactly what a mapping of Address.text
    /// would have handed over.
    /// </summary>
    [Fact]
    public void An_element_is_reduced_to_its_text_for_a_node_that_converts_a_scalar()
    {
        const string addressJson =
            """
            {"use":"home","type":"postal",
             "text":"1563 W Main St Apt 322\r\nKENTWOOD MI 49518\r\nUnited States of America",
             "line":["1563 West Main St","Apt 322"],"city":"KENTWOOD","district":"KENT","state":"MI",
             "postalCode":"49518","country":"US"}
            """;

        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new AddressParsingNode(), addressJson, new Dictionary<string, string>(), null, TransformArrayMode.Whole);

        result.Success.Should().BeTrue();
        var address = (JsonObject)result.Value!;
        address["city"]!.GetValue<string>().Should().Be("KENTWOOD");
        address["state"]!.GetValue<string>().Should().Be("MI");
        address["postalCode"]!.GetValue<string>().Should().Be("49518");
        address.ToJsonString().Should().NotContain("\\u0022", "no JSON source text is being parsed as data any more");
    }

    /// <summary>
    /// The invariant the whole "same logic" change exists for: mapping an element and mapping that element's
    /// own .text must produce byte-identical output for the same rule. The first is what the popover calls a
    /// whole-node mapping (Patient.address -> Add_3), the second an ordinary field mapping
    /// (Patient.address.text -> Add_2). Asserted across every scalar node, so a node added later cannot
    /// quietly reintroduce the split.
    /// </summary>
    [Fact]
    public void Mapping_an_element_and_mapping_its_text_produce_the_same_output()
    {
        const string text = "1563 W Main St Apt 322\r\nKENTWOOD MI 49518\r\nUnited States of America";
        const string element =
            """
            {"use":"home","type":"postal",
             "text":"1563 W Main St Apt 322\r\nKENTWOOD MI 49518\r\nUnited States of America",
             "line":["1563 West Main St","Apt 322"],"city":"KENTWOOD","district":"KENT","state":"MI",
             "postalCode":"49518","country":"US"}
            """;

        var config = new Dictionary<string, string> { ["use"] = "home", ["country"] = "US" };

        foreach (ITransformNode node in new ITransformNode[] { new AddressParsingNode(), new StringNormalizationNode() })
        {
            var fromText = TransformNodeApplier.ExecuteWithArrayMode(
                node, text, config, null, TransformArrayMode.Whole);
            var fromElement = TransformNodeApplier.ExecuteWithArrayMode(
                node, element, config, null, TransformArrayMode.Whole);

            fromElement.Success.Should().Be(fromText.Success, $"{node.NodeType} must not differ by which mapping fed it");
            Render(fromElement).Should().Be(Render(fromText), $"{node.NodeType} must not differ by which mapping fed it");
        }
    }

    private static string? Render(TransformResult result) => result.Value switch
    {
        null => null,
        JsonNode node => node.ToJsonString(),
        var other => other.ToString(),
    };

    /// <summary>An element with no `text` is left whole — inventing a scalar for it would be guesswork, and it
    /// may be a shape the node genuinely reads (a structured name from an earlier rule in the chain).</summary>
    [Fact]
    public void An_element_with_no_text_is_left_alone()
    {
        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new HumanNameParsingNode(), """{"family":"Roe","given":["Jane"]}""",
            new Dictionary<string, string> { ["format"] = "First Last" }, null, TransformArrayMode.Whole);

        result.Value.Should().Be("Jane Roe");
    }

    [Fact]
    public void PerItem_leaves_an_ordinary_string_alone()
    {
        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new StringNormalizationNode(), "roe",
            new Dictionary<string, string> { ["case"] = "upper" }, null, TransformArrayMode.PerItem);

        result.Value.Should().Be("ROE", "a string is one value, never a collection of characters");
    }

    [Fact]
    public void PerItem_leaves_a_string_that_merely_starts_with_a_bracket_alone()
    {
        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new StringNormalizationNode(), "[not json",
            new Dictionary<string, string> { ["case"] = "upper" }, null, TransformArrayMode.PerItem);

        result.Value.Should().Be("[NOT JSON");
    }

    [Fact]
    public void PerItem_fans_out_over_a_JSON_array_of_plain_strings()
    {
        var result = TransformNodeApplier.ExecuteWithArrayMode(
            new StringNormalizationNode(), """["roe","doe"]""",
            new Dictionary<string, string> { ["case"] = "upper" }, null, TransformArrayMode.PerItem);

        ((object?[])result.Value!).Should().Equal("ROE", "DOE");
    }

    [Fact]
    public async Task PerItem_reports_each_element_to_the_lineage_callback()
    {
        var seen = new List<object?>();

        await TransformNodeApplier.ExecuteWithArrayModeAsync(
            new HumanNameParsingNode(), NameArrayJson,
            new Dictionary<string, string> { ["pattern"] = "FirstLast" }, null, TransformArrayMode.PerItem,
            CancellationToken.None, onItemExecuted: (input, _) => seen.Add(input));

        seen.Should().HaveCount(2, "lineage must still see which element produced which result");
    }
}
