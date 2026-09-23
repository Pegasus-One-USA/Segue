using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// JsonMappingEngine.ConvertDate/ConvertDateOnly — the no-transform-rule path (mirrors
/// MappingNodeExecutor.CoerceToExpectedValueType/CoerceDate's own fixes; see that type's tests for the
/// rule-chain equivalent of this coverage).
///
/// ConvertDate (DateTime-typed fields) originally parsed with DateTimeStyles.AssumeUniversal alone, which
/// converts an offset-bearing input to THIS PROCESS's own local system time zone before returning Kind=Local
/// — so the exact same FHIR instant resolved to a different value depending on which machine happened to run
/// it. Verified empirically on a UTC+05:30 host: "2026-03-14T22:00:00Z" resolved to 2026-03-15T03:30 local.
///
/// ConvertDateOnly (Date-typed fields, split out from ConvertDate) instead never normalizes through UTC at
/// all — a FHIR `date` has no time zone, so DateTimeOffset-based parsing (which never does the system-local
/// conversion DateTime.TryParse does) preserves the calendar date exactly as written, and a partial date
/// ("2020"/"2020-05") is left unconverted rather than having a day fabricated for it.
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
    public void A_negative_offset_input_still_resolves_the_correct_UTC_date_and_time()
    {
        const string json = """{"resourceType":"Patient","value":"2027-06-17T16:45:47.000-04:00"}""";

        var result = new JsonMappingEngine().Map(json, [Field(MappingValueType.DateTime)]);

        result.Errors.Should().BeEmpty();
        var value = result.Values["Value"].Should().BeOfType<DateTime>().Subject;
        value.Should().Be(new DateTime(2027, 6, 17, 20, 45, 47));
    }

    /// <summary>
    /// A Date-typed field (ConvertDateOnly) must NOT normalize through UTC the way DateTime does — a FHIR
    /// `date` has no time zone at all, so the calendar date the source wrote IS the value. Regression guard
    /// for a bug introduced by the very first version of this fix: parsing with DateTime.TryParse's
    /// AdjustToUniversal converted an offset-bearing input to UTC first, which changes the CALENDAR DAY
    /// itself — "2026-03-14T20:00:00-05:00" (a -05:00 offset) would incorrectly resolve to 2026-03-15.
    /// </summary>
    [Fact]
    public void Date_typed_field_keeps_the_calendar_date_as_written_not_UTC_normalized()
    {
        const string json = """{"resourceType":"Patient","value":"2026-03-14T20:00:00-05:00"}""";

        var result = new JsonMappingEngine().Map(json, [Field(MappingValueType.Date)]);

        result.Errors.Should().BeEmpty();
        var value = result.Values["Value"].Should().BeOfType<DateTime>().Subject;
        value.Should().Be(new DateTime(2026, 3, 14));
    }

    [Theory]
    [InlineData("2020")]
    [InlineData("2020-05")]
    public void Date_typed_field_passes_a_partial_FHIR_date_through_unconverted_rather_than_fabricating_a_day(string partial)
    {
        // "2020"/"2020-05" are valid FHIR `date` precision on their own — DateTimeFormatNode deliberately
        // emits them unchanged rather than invent a day (or month). Coercing them into a full date here
        // would silently fabricate real patient data, so the raw partial string must survive instead.
        var json = $$"""{"resourceType":"Patient","value":"{{partial}}"}""";

        var result = new JsonMappingEngine().Map(json, [Field(MappingValueType.Date)]);

        result.Errors.Should().ContainSingle(e => e.Contains("year/year-month precision"));
        result.Values["Value"].Should().Be(partial);
    }
}
