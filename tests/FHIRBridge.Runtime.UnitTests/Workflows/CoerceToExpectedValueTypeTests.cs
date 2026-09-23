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
    public void DateTime_arm_produces_Kind_Unspecified_not_Utc_or_Local()
    {
        // Kind drives Npgsql's wire type: Utc -> "timestamptz" (a plain "timestamp" column then throws
        // 42804); Local and Unspecified both -> "timestamp", but Local is reached by parsing straight into
        // the HOST MACHINE's own time zone (host-dependent — two hosts store two different instants for the
        // same input). Unspecified, reached only after first resolving the true UTC instant, is the one
        // Kind that is both accepted by a plain "timestamp" column AND deterministic across hosts.
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "2026-03-14T22:00:00Z", MappingValueType.DateTime, DestinationType.PostgreSql);

        var dateTime = result.Should().BeOfType<DateTime>().Subject;
        dateTime.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public void DateTime_arm_resolves_the_true_UTC_instant_regardless_of_an_explicit_offset()
    {
        // Parsing must land on the UTC wall-clock reading of the instant, not the offset stripped verbatim
        // or converted to whatever time zone the host process happens to run in.
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "2026-03-14T22:00:00Z", MappingValueType.DateTime, DestinationType.PostgreSql);

        var dateTime = result.Should().BeOfType<DateTime>().Subject;
        dateTime.Should().Be(new DateTime(2026, 3, 14, 22, 0, 0));
    }

    [Fact]
    public void Date_arm_produces_Kind_Unspecified()
    {
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "2026-03-14T22:00:00Z", MappingValueType.Date, DestinationType.PostgreSql);

        var date = result.Should().BeOfType<DateTime>().Subject;
        date.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Unlike DateTime, the Date arm must NOT normalize through UTC — a FHIR `date` has no time zone at all,
    /// so the calendar date the source wrote IS the value. Regression guard for a bug introduced by the very
    /// first version of this fix: parsing with AdjustToUniversal converted an offset-bearing input to UTC
    /// first, which changes the CALENDAR DAY itself for a non-zero offset ("2026-03-14T20:00:00-05:00" would
    /// incorrectly resolve to 2026-03-15).
    /// </summary>
    [Fact]
    public void Date_arm_keeps_the_calendar_date_as_written_not_UTC_normalized()
    {
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "2026-03-14T20:00:00-05:00", MappingValueType.Date, DestinationType.PostgreSql);

        var date = result.Should().BeOfType<DateTime>().Subject;
        date.Should().Be(new DateTime(2026, 3, 14));
    }

    [Theory]
    [InlineData("2020")]
    [InlineData("2020-05")]
    public void Date_arm_passes_a_partial_FHIR_date_through_unconverted_rather_than_fabricating_a_day(string partial)
    {
        // "2020"/"2020-05" are valid FHIR `date` precision on their own — DateTimeFormatNode deliberately
        // emits them unchanged rather than invent a day (or month). Coercing them into a full date here
        // would silently fabricate real patient data, so the raw partial string must survive instead — a
        // strict `date` column genuinely can't hold partial precision without fabricating it, and surfacing
        // that as a visible 42804 is more honest than a fabricated day that reads as if it were real.
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            partial, MappingValueType.Date, DestinationType.PostgreSql);

        result.Should().Be(partial);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("Y", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Boolean_arm_accepts_BooleanConversionNodes_own_default_spellings(string raw, bool expected)
    {
        // bool.TryParse alone only recognizes literal "True"/"False" — BooleanConversionNode's own defaults
        // ("y,yes,1,t,true,+" / "n,no,0,f,false,-") must also be accepted, or the exact spellings that node is
        // configured to treat as a boolean still 42804 into a Postgres boolean column.
        var result = MappingNodeExecutor.CoerceToExpectedValueType(raw, MappingValueType.Boolean, DestinationType.PostgreSql);

        result.Should().Be(expected);
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
        // A node that already self-types (DateMathAge's "age" operation -> int, BooleanConversion -> a
        // native bool straight from its own Execute) must never be re-coerced — only a rule chain's raw
        // string output is ever a coercion candidate.
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

    [Fact]
    public void An_unrecognized_boolean_spelling_is_returned_unchanged_rather_than_throwing()
    {
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "maybe", MappingValueType.Boolean, DestinationType.PostgreSql);

        result.Should().Be("maybe");
    }

    [Fact]
    public void Boolean_arm_reads_a_rules_own_custom_spellings_from_its_ConfigJson()
    {
        // BooleanConversion's trueValues/falseValues are per-rule configurable (see its own Execute) — a rule
        // customized to accept "oui"/"non" must not fall back to the node's stock "y,yes,1,..." defaults.
        var configJson = """{"trueValues":"oui","falseValues":"non"}""";

        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "oui", MappingValueType.Boolean, DestinationType.PostgreSql, configJson);

        result.Should().Be(true);
    }

    [Fact]
    public void Boolean_arm_falls_back_to_defaults_when_no_rule_config_is_supplied()
    {
        var result = MappingNodeExecutor.CoerceToExpectedValueType(
            "yes", MappingValueType.Boolean, DestinationType.PostgreSql, typedRuleConfigJson: null);

        result.Should().Be(true);
    }
}
