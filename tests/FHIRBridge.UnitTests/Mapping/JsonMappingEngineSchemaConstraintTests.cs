using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// <c>MappingFieldDto.MaxLength</c>/<c>Precision</c>/<c>Scale</c> are filled in at pipeline-run time from the
/// destination's live column metadata (see <c>ConfiguredPipelineService.EnrichWithDestinationSchemaAsync</c>), not
/// persisted on the mapping itself. When present, <c>JsonMappingEngine</c> rejects a value that would overflow the
/// destination column — a truncation/numeric-overflow SqlException caught before it ever reaches the database.
/// </summary>
public sealed class JsonMappingEngineSchemaConstraintTests
{
    private static readonly JsonMappingEngine Sut = new();

    [Fact]
    public void String_value_within_max_length_passes()
    {
        var json = """{ "code": { "text": "short text" } }""";
        var field = new MappingFieldDto("Code", "$.code.text", MappingValueType.String, false, null, null, MaxLength: 50);

        var result = Sut.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["Code"].Should().Be("short text");
    }

    [Fact]
    public void String_value_exceeding_max_length_is_rejected()
    {
        var longText = "On average, how many days per week do you engage in strenuous exercise";
        var json = $$"""{ "code": { "text": "{{longText}}" } }""";
        var field = new MappingFieldDto("Code", "$.code.text", MappingValueType.String, false, null, null, MaxLength: 50);

        var result = Sut.Map(json, [field]);

        result.Errors.Should().ContainSingle(e => e.Contains("characters but the destination column allows at most 50"));
        result.Values["Code"].Should().BeNull();
    }

    [Fact]
    public void No_max_length_known_performs_no_length_check()
    {
        var longText = new string('x', 500);
        var json = $$"""{ "code": { "text": "{{longText}}" } }""";
        var field = new MappingFieldDto("Code", "$.code.text", MappingValueType.String, false, null, null);

        var result = Sut.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["Code"].Should().Be(longText);
    }

    [Fact]
    public void Decimal_value_within_precision_and_scale_passes()
    {
        var json = """{ "valueQuantity": { "value": 123.45 } }""";
        var field = new MappingFieldDto(
            "Value", "$.valueQuantity.value", MappingValueType.Decimal, false, null, null, Precision: 5, Scale: 2);

        var result = Sut.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["Value"].Should().Be(123.45m);
    }

    [Fact]
    public void Decimal_value_exceeding_precision_is_rejected()
    {
        var json = """{ "valueQuantity": { "value": 12345.67 } }""";
        var field = new MappingFieldDto(
            "Value", "$.valueQuantity.value", MappingValueType.Decimal, false, null, null, Precision: 5, Scale: 2);

        var result = Sut.Map(json, [field]);

        result.Errors.Should().ContainSingle(e => e.Contains("exceeds the destination column's numeric precision"));
        result.Values["Value"].Should().BeNull();
    }

    [Fact]
    public void No_precision_known_performs_no_numeric_overflow_check()
    {
        var json = """{ "valueQuantity": { "value": 999999.999 } }""";
        var field = new MappingFieldDto("Value", "$.valueQuantity.value", MappingValueType.Decimal, false, null, null);

        var result = Sut.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["Value"].Should().Be(999999.999m);
    }
}
