using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>Repro for a live bug report: Patient.birthDate ("1993-08-18") maps to null in dbo.Patient_NewMapped.
/// The MappingField's ValueType is "Date" (not "String") — JsonMappingEngine.ConvertElement's Date case does
/// `ConvertDate(...)?.Date`, which returns a boxed .NET DateTime, not a string. DateTimeFormatNode then does
/// `value?.ToString()` with no format/culture — DateTime.ToString() uses CultureInfo.CurrentCulture, not
/// InvariantCulture, so its output format depends on the server's default culture.</summary>
public sealed class DateTimeFormatBirthDateReproTests
{
    [Fact]
    public void String_input_does_not_fail()
    {
        var config = new Dictionary<string, string> { ["targetType"] = "dateTime", ["allowPartialDate"] = "False" };
        var result = new DateTimeFormatNode().Execute("1993-08-18", config, null);
        result.Success.Should().BeTrue(result.Error);
    }

    [Fact]
    public void Boxed_DateTime_input_as_JsonMappingEngine_actually_produces_for_ValueType_Date()
    {
        var config = new Dictionary<string, string> { ["targetType"] = "dateTime", ["allowPartialDate"] = "False" };
        object boxedDate = new DateTime(1993, 8, 18); // exactly what ConvertElement's Date case yields
        var result = new DateTimeFormatNode().Execute(boxedDate, config, null);
        result.Success.Should().BeTrue(result.Error);
        result.Value.Should().Be("1993-08-18T00:00:00+00:00");
    }
}
