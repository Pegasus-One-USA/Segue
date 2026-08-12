using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// Repro for a live bug report: Patient.birthDate ("1993-08-18") mapped to NULL in dbo.Patient_NewMapped.
/// Root cause: ConvertDate treated field.Format as a DateTime.TryParseExact pattern, but Format is actually
/// the column-mode marker ("directField") BuildJsonPathAndFormat stamps onto every field — never a valid
/// date pattern, so TryParseExact always failed for any Date/DateTime-typed field mapped the normal way.
/// </summary>
public sealed class JsonMappingEngineDateFieldReproTests
{
    private const string PatientJson = """{"resourceType":"Patient","id":"egqBHVfQlt4Bw3XGXoxVxHg3","birthDate":"1993-08-18"}""";

    [Fact]
    public void Date_typed_field_with_the_real_directField_mode_marker_as_Format_still_parses()
    {
        var field = new MappingFieldDto(
            TargetField: "BirthDate",
            JsonPath: "$.birthDate",
            ValueType: MappingValueType.Date,
            IsRequired: false,
            DefaultValue: null,
            Format: "directField", // the exact value BuildJsonPathAndFormat stamps, and what's live in the DB
            ResourceType: "Patient",
            DestinationObject: "Patient_NewMapped",
            ArrayPolicy: ArrayPolicy.Scalar);

        var result = new JsonMappingEngine().Map(PatientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values.Should().ContainKey("BirthDate").WhoseValue.Should().Be(new DateTime(1993, 8, 18));
    }
}
