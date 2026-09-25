using System.Text.Json.Nodes;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// The agreed behaviour for a HumanNameParsing rule on an ARRAY field (a whole-node mapping of Patient.name),
/// against the real Epic payload. The element already states its own parts, so nothing is parsed out of the
/// display string — `pattern` selects the order those parts are composed in, and the result is a display
/// string. The one thing still read from `text` is the suffix, which FHIR leaves nowhere else here.
/// </summary>
public sealed class HumanNameArrayModeSpecTests
{
    private const string NameArray =
        """
        [{"use":"official","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]},
         {"use":"usual","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]},
         {"use":"old","text":"MCGINNIS,WARREN","family":"McGinnis","given":["Warren"]}]
        """;

    private const string FirstInstance =
        """{"use":"official","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]}""";

    private static object? Run(string value, string pattern, TransformArrayMode mode) =>
        TransformNodeApplier.ExecuteWithArrayMode(
            new HumanNameParsingNode(), value,
            new Dictionary<string, string> { ["pattern"] = pattern }, null, mode).Value;

    [Theory]
    [InlineData("FirstLast", "Warren McGinnis")]
    [InlineData("FirstMiddleLast", "Warren James McGinnis")]
    [InlineData("FirstLastMiddle", "Warren McGinnis James")]
    [InlineData("LastFirstMiddle", "McGinnis Warren James")]
    [InlineData("RoundTrip", "Warren James McGinnis III")]
    public void A_single_instance_composes_the_order_the_mode_names(string pattern, string expected)
    {
        Run(FirstInstance, pattern, TransformArrayMode.Whole).Should().Be(expected);
    }

    /// <summary>
    /// "All records" renders every repeat, one result per element. Joining those into the single delimited
    /// string the column receives is the write step's job, not this one — see
    /// MappingNodeExecutorTests.All_records_writes_every_transformed_name_as_one_delimited_string.
    /// </summary>
    [Theory]
    [InlineData("FirstLast", new[] { "Warren McGinnis", "Warren McGinnis", "Warren McGinnis" })]
    [InlineData("FirstMiddleLast", new[] { "Warren James McGinnis", "Warren James McGinnis", "Warren McGinnis" })]
    [InlineData("LastFirstMiddle", new[] { "McGinnis Warren James", "McGinnis Warren James", "McGinnis Warren" })]
    public void All_records_renders_every_repeat(string pattern, string[] expected)
    {
        Run(NameArray, pattern, TransformArrayMode.PerItem).Should().BeEquivalentTo(expected);
    }

    /// <summary>The third name has one given name only, so the modes that would place a middle name simply
    /// have none to place — no stray separator is left behind.</summary>
    [Fact]
    public void A_name_with_no_middle_name_renders_without_a_gap()
    {
        const string single = """{"use":"old","text":"MCGINNIS,WARREN","family":"McGinnis","given":["Warren"]}""";

        Run(single, "FirstLastMiddle", TransformArrayMode.Whole).Should().Be("Warren McGinnis");
        Run(single, "RoundTrip", TransformArrayMode.Whole).Should().Be("Warren McGinnis");
    }

    /// <summary>The suffix comes out of `text` because the element never states it — without that, RoundTrip
    /// could not produce the "III" the agreed example ends with.</summary>
    [Fact]
    public void RoundTrip_recovers_a_suffix_the_element_does_not_state()
    {
        FirstInstance.Should().NotContain("suffix");

        Run(FirstInstance, "RoundTrip", TransformArrayMode.Whole).Should().Be("Warren James McGinnis III");
    }

    /// <summary>A prefix the element DOES state is used as-is, ahead of the given names.</summary>
    [Fact]
    public void RoundTrip_places_a_stated_prefix_first()
    {
        const string withPrefix =
            """{"prefix":["Dr"],"family":"Roe","given":["Jane"],"text":"Dr Jane Roe"}""";

        Run(withPrefix, "RoundTrip", TransformArrayMode.Whole).Should().Be("Dr Jane Roe");
    }

    /// <summary>No transformation at all is not this node's path — the mapping writes the node's JSON itself
    /// (ArrayPolicy.StoreJson). Asserted here only to state the boundary: everything above needs a rule.</summary>
    [Fact]
    public void Without_a_rule_nothing_here_runs()
    {
        // The array reaches no node, so its JSON is what the column receives. Guard against a future change
        // that quietly makes the applier act on a value no rule asked it to touch.
        TransformNodeApplier.ExecuteWithArrayMode(
                new HumanNameParsingNode(), NameArray, new Dictionary<string, string>(), null,
                TransformArrayMode.Whole)
            .Success.Should().BeFalse("an array handed to a scalar node without per-item mode is a mismatch");
    }
}
