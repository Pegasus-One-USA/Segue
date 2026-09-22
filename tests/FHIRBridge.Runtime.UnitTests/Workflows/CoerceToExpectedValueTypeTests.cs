using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// MappingNodeExecutor.CoerceToExpectedValueType — coerces a transform rule chain's string output back to a
/// native CLR type before a relational destination writer sees it (fixes Postgres 42804 on Date/DateTime/
/// Integer/Decimal/Boolean-declared fields). Exercises it directly (internal, via InternalsVisibleTo) rather
/// than through the full executor, since standing it up needs a rule resolver, node registry and lineage
/// dispatcher this coercion step doesn't otherwise touch.
/// </summary>
public sealed class CoerceToExpectedValueTypeTests
{
    [Fact]
    public void DateTime_arm_produces_Kind_Local_not_Utc()
    {
        // Kind drives Npgsql's wire type: Utc -> "timestamptz", Local/Unspecified -> "timestamp". A destination
        // column normalized from "datetime2"/"datetime" is a plain "timestamp" (PostgreSqlDdlTypeValidator), so
        // Kind must match JsonMappingEngine.ConvertDate's own AssumeUniversal-alone parsing (Kind=Local) exactly
        // — not AdjustToUniversal|AssumeUniversal (Kind=Utc), which would reintroduce the 42804 this exists to
        // prevent.
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "2026-03-14T10:00:00", MappingValueType.DateTime, DestinationType.PostgreSql);

        var dateTime = result.Should().BeOfType<DateTime>().Subject;
        dateTime.Kind.Should().Be(DateTimeKind.Local);
    }

    [Fact]
    public void Date_arm_resolves_the_UTC_calendar_date_regardless_of_an_explicit_offset()
    {
        // An offset-bearing input must land on the same calendar date no matter which time zone this process
        // happens to run in — DateTimeStyles.None would instead convert to server-local first, making the
        // stored date depend on the host machine (UTC+05:30 rolls this to the 15th, a UTC host keeps the 14th).
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "2026-03-14T22:00:00Z", MappingValueType.Date, DestinationType.PostgreSql);

        var date = result.Should().BeOfType<DateTime>().Subject;
        date.Should().Be(new DateTime(2026, 3, 14));
    }

    [Theory]
    [InlineData(DestinationType.SqlServer)]
    [InlineData(DestinationType.AzureSql)]
    [InlineData(DestinationType.PostgreSql)]
    [InlineData(DestinationType.MySql)]
    [InlineData(DestinationType.DataFabricWarehouse)]
    public void Coerces_for_every_relational_destination(DestinationType destinationType)
    {
        var result = MappingNodeExecutor.CoerceToExpectedValueType("42", MappingValueType.Integer, destinationType);

        result.Should().Be(42L);
    }

    [Theory]
    [InlineData(DestinationType.Csv)]
    [InlineData(DestinationType.BlobStorage)]
    [InlineData(DestinationType.Mongo)]
    [InlineData(DestinationType.FhirRepository)]
    public void Leaves_the_string_untouched_for_a_non_relational_destination(DestinationType destinationType)
    {
        // MappedDestinationSerialization.ToMappedOnlyCsv/ToCsv format a value with a bare .ToString() — coercing
        // to a native DateTime here would make a CSV/Blob export silently switch from "2026-03-14" to a
        // culture-formatted "3/14/2026 12:00:00 AM". This coercion exists only to satisfy a relational engine's
        // strict column typing, so every other destination must see its rule chain's string output unchanged.
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "2026-03-14", MappingValueType.Date, destinationType);

        result.Should().Be("2026-03-14");
    }

    [Fact]
    public void Null_expected_type_passes_the_value_through_unchanged()
    {
        var result = MappingNodeExecutor.CoerceToExpectedValueType("raw", null, DestinationType.PostgreSql);

        result.Should().Be("raw");
    }

    [Fact]
    public void A_non_string_value_passes_through_unchanged_even_with_a_relational_destination()
    {
        // A node that already self-types (DateMathAge's "age" operation -> int, BooleanConversion -> bool) must
        // never be re-coerced — only a rule chain's raw string output is ever a coercion candidate.
        var result = MappingNodeExecutor.CoerceToExpectedValueType(7, MappingValueType.Integer, DestinationType.PostgreSql);

        result.Should().Be(7);
    }

    [Fact]
    public void An_unparsable_string_is_returned_unchanged_rather_than_throwing()
    {
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "not-a-number", MappingValueType.Integer, DestinationType.PostgreSql);

        result.Should().Be("not-a-number");
    }
}
