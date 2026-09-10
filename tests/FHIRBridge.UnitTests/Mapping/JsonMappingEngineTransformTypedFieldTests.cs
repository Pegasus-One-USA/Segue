using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// Covers <see cref="MappingFieldDto.DeferTypeToTransform"/>: a column whose real value is produced by a
/// transformation rule (Patient.birthDate -&gt; an int age via DateMathAge) is stored with the RULE's output
/// type, because that's what reaches the column. Extraction runs before the rules do, so coercing to that type
/// here can only fail — and the resulting mapping error makes ConfiguredPipelineService skip the whole
/// resource, turning a correctly-mapped field into silently missing patients.
/// </summary>
public sealed class JsonMappingEngineTransformTypedFieldTests
{
    private const string PatientJson = """{"resourceType":"Patient","id":"p1","birthDate":"1980-05-01"}""";

    private static MappingFieldDto AgeField(bool deferTypeToTransform) => new(
        TargetField: "PatientAge",
        JsonPath: "$.birthDate",
        ValueType: MappingValueType.Integer,
        IsRequired: false,
        DefaultValue: null,
        Format: "directField",
        ResourceType: "Patient",
        DestinationObject: "Patient_NewMapped",
        ArrayPolicy: ArrayPolicy.Scalar,
        DeferTypeToTransform: deferTypeToTransform);

    [Fact]
    public void Rule_typed_field_passes_the_raw_value_through_without_a_conversion_error()
    {
        var result = new JsonMappingEngine().Map(PatientJson, [AgeField(deferTypeToTransform: true)]);

        result.Errors.Should().BeEmpty();
        result.Values.Should().ContainKey("PatientAge").WhoseValue.Should().Be("1980-05-01");
    }

    /// <summary>The pre-existing behaviour, unchanged for every field that has no rule behind it: the value
    /// still survives (the raw-value fallback), but the failed coercion is reported — which is what makes the
    /// flag above necessary rather than cosmetic.</summary>
    [Fact]
    public void Same_field_without_the_flag_still_records_a_conversion_error()
    {
        var result = new JsonMappingEngine().Map(PatientJson, [AgeField(deferTypeToTransform: false)]);

        result.Errors.Should().ContainSingle().Which.Should().Contain("PatientAge");
    }
}
