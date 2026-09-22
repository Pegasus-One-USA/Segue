using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// JsonMappingEngine.ConvertDate — the no-transform-rule path (mirrors
/// MappingNodeExecutor.CoerceToExpectedValueType's own DateTime-arm fix; see that type's tests for the
/// rule-chain equivalent of this coverage).
///
/// Before this fix, ConvertDate parsed with DateTimeStyles.AssumeUniversal alone, which converts an
/// offset-bearing input to THIS PROCESS's own local system time zone before returning Kind=Local — so the
/// exact same FHIR instant resolved to a different value (and, for a Date-typed field whose caller then
/// truncates to `.Date`, potentially a different CALENDAR DAY) depending on which machine happened to run
/// it. Verified empirically on a UTC+05:30 host: "2026-03-14T22:00:00Z" resolved to 2026-03-15T03:30 local
/// — a full day later than the correct UTC date.
/// </summary>
public sealed class JsonMappingEngineDateTimeZoneTests
{
    private static MappingFieldDto Field(MappingValueType valueType) => new(
        TargetField: "Value",
        JsonPath: "$.value",
        ValueType: valueType,
        IsRequired: false,
        DefaultValue: null,
        Format: "directField",
        ResourceType: "Patient",
        DestinationObject: "Patient_Test",
        ArrayPolicy: ArrayPolicy.Scalar);

    [Fact]
    public void DateTime_typed_field_resolves_the_true_UTC_instant_with_Kind_Unspecified()
    {
        const string json = """{"resourceType":"Patient","value":"2026-03-14T22:00:00Z"}""";

        var result = new JsonMappingEngine().Map(json, [Field(MappingValueType.DateTime)]);

        result.Errors.Should().BeEmpty();
        var value = result.Values["Value"].Should().BeOfType<DateTime>().Subject;
        value.Kind.Should().Be(DateTimeKind.Unspecified);
        value.Should().Be(new DateTime(2026, 3, 14, 22, 0, 0));
    }

    [Fact]
    public void Date_typed_field_resolves_the_UTC_calendar_date_not_a_host_shifted_one()
    {
        // The regression this specifically guards: a Date-typed field truncates ConvertDate's result to
        // `.Date` (see JsonMappingEngine.ConvertValue) — on the pre-fix Kind=Local behavior, an evening-UTC
        // instant rolled to the NEXT calendar day on any host east of UTC.
        const string json = """{"resourceType":"Patient","value":"2026-03-14T22:00:00Z"}""";

        var result = new JsonMappingEngine().Map(json, [Field(MappingValueType.Date)]);

        result.Errors.Should().BeEmpty();
        var value = result.Values["Value"].Should().BeOfType<DateTime>().Subject;
        value.Should().Be(new DateTime(2026, 3, 14));
    }

    [Fact]
    public void A_negative_offset_input_still_resolves_the_correct_UTC_date_and_time()
    {
        const string json = """{"resourceType":"Patient","value":"2027-06-17T16:45:47.000-04:00"}""";

        var result = new JsonMappingEngine().Map(json, [Field(MappingValueType.DateTime)]);

        result.Errors.Should().BeEmpty();
        var value = result.Values["Value"].Should().BeOfType<DateTime>().Subject;
        value.Should().Be(new DateTime(2027, 6, 17, 20, 45, 47));
    }
}
