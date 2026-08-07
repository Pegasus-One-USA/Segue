using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class ValueTypeNodesTests
{
    [Theory]
    [InlineData("03/14/2026", "date", "2026-03-14")]
    public void DateTimeFormatNode_parses_and_reformats(string input, string targetType, string expected)
    {
        var result = new DateTimeFormatNode().Execute(input, new Dictionary<string, string> { ["targetType"] = targetType }, null);
        result.Success.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Fact]
    public void DateTimeFormatNode_returns_null_for_empty_input()
    {
        new DateTimeFormatNode().Execute("", new Dictionary<string, string>(), null).Value.Should().BeNull();
    }

    [Fact]
    public void NumberCastNode_strips_separators_and_preserves_precision()
    {
        var result = new NumberCastNode().Execute("1,234.50", new Dictionary<string, string> { ["targetType"] = "decimal" }, null);
        result.Value.Should().Be(1234.50m);
    }

    [Fact]
    public void NumberCastNode_returns_null_not_zero_for_empty_string()
    {
        new NumberCastNode().Execute("", new Dictionary<string, string>(), null).Value.Should().BeNull();
    }

    [Theory]
    [InlineData("Y", true)]
    [InlineData("0", false)]
    [InlineData("maybe", null)]
    public void BooleanConversionNode_maps_tokens(string input, bool? expected)
    {
        new BooleanConversionNode().Execute(input, new Dictionary<string, string>(), null).Value.Should().Be(expected);
    }

    [Fact]
    public void UnitConversionNode_converts_pounds_to_kilograms()
    {
        var config = new Dictionary<string, string> { ["sourceUnit"] = "lb_av", ["targetUnit"] = "kg", ["precision"] = "1" };
        var result = new UnitConversionNode().Execute(185m, config, null);
        var quantity = (System.Text.Json.Nodes.JsonObject)result.Value!;
        quantity["value"]!.GetValue<decimal>().Should().Be(83.9m);
        quantity["system"]!.GetValue<string>().Should().Be("http://unitsofmeasure.org");
    }

    [Fact]
    public void QuantityRangeAssemblyNode_parses_a_leading_comparator()
    {
        var result = new QuantityRangeAssemblyNode().Execute("<0.5", new Dictionary<string, string> { ["unit"] = "mg/L" }, null);
        var quantity = (System.Text.Json.Nodes.JsonObject)result.Value!;
        quantity["comparator"]!.GetValue<string>().Should().Be("<");
        quantity["value"]!.GetValue<decimal>().Should().Be(0.5m);
    }

    [Fact]
    public void QuantityRangeAssemblyNode_parses_a_range()
    {
        var result = new QuantityRangeAssemblyNode().Execute("10-20", new Dictionary<string, string>(), null);
        var range = (System.Text.Json.Nodes.JsonObject)result.Value!;
        range["low"]!["value"]!.GetValue<decimal>().Should().Be(10m);
        range["high"]!["value"]!.GetValue<decimal>().Should().Be(20m);
    }

    [Fact]
    public void RoundingScalingNode_rounds_half_up_to_configured_places()
    {
        var config = new Dictionary<string, string> { ["decimalPlaces"] = "1" };
        new RoundingScalingNode().Execute(83.914m, config, null).Value.Should().Be(83.9m);
    }
}
