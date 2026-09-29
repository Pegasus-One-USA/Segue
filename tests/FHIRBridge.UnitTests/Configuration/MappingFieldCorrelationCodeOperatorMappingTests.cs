using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Regression guard found on review: <c>CorrelationCodeOperator</c> was added to <c>MappingFieldDto</c> and to
/// <c>JsonMappingEngine</c>'s matching logic, but <c>ConfigurationMapper.ToDomain</c>/<c>ToDto</c> never carried it
/// between the persisted domain <c>MappingField</c> and the runtime DTO — any "Match criteria" field saved through
/// a real <c>MappingProfile</c> (the Configured Pipeline execution path, or a workflow node that references a
/// profile by id rather than embedding its fields inline — see TransformNodeExecutors.ResolveResourceMappingAsync's
/// id-based fallback) silently dropped the user's chosen operator ("contains"/"not equals") back to the engine's
/// own default, exact match, on every real save — with no error, and no test catching it, since every existing test
/// constructed a <c>MappingFieldDto</c> by hand rather than round-tripping it through this mapper.
/// </summary>
public sealed class MappingFieldCorrelationCodeOperatorMappingTests
{
    [Theory]
    [InlineData("Contains")]
    [InlineData("NotEquals")]
    [InlineData("Equals")]
    [InlineData(null)]
    public void ToDomain_then_ToDto_round_trips_the_correlation_operator(string? correlationCodeOperator)
    {
        var dto = new MappingFieldDto(
            TargetField: "Telecom",
            JsonPath: "$.contact[*].telecom[*].value",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.contact[*].telecom[*].system",
            CorrelationCodeValue: "email",
            CorrelationCodeOperator: correlationCodeOperator);

        var domain = ConfigurationMapper.ToDomain(dto);
        domain.CorrelationCodeOperator.Should().Be(correlationCodeOperator);

        var roundTripped = ConfigurationMapper.ToDto(domain);
        roundTripped.CorrelationCodeOperator.Should().Be(correlationCodeOperator);
    }

    [Fact]
    public void ToDto_carries_the_operator_through_for_a_field_that_was_never_converted_from_a_dto()
    {
        var field = new MappingField(
            "Telecom", "$.contact[*].telecom[*].value", MappingValueType.String,
            IsRequired: false, DefaultValue: null, Format: null,
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.contact[*].telecom[*].system",
            CorrelationCodeValue: "email",
            CorrelationCodeOperator: "NotEquals");

        var dto = ConfigurationMapper.ToDto(field);

        dto.CorrelationCodeOperator.Should().Be("NotEquals");
    }
}
