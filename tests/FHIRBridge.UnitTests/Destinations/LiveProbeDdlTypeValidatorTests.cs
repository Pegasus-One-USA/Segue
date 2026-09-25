using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// The Edit-column modal feeds a column's own live-probed type straight back as a candidate value (see
/// field-mapping-edit-column-modal.component.ts) — for a sized/precise type, that live-probed spelling is
/// the ENGINE's own bare type name (size/precision live in separate information_schema columns neither
/// validator is ever handed), not the "type(size)" shape the Add-column/Create-table pickers themselves
/// produce. Before this fix only "timestamp without time zone" was recognized; re-saving (or editing)
/// almost any OTHER existing sized column untouched still threw "not an allowed data type."
/// </summary>
public sealed class LiveProbeDdlTypeValidatorTests
{
    [Theory]
    [InlineData("timestamp without time zone", "timestamp")]
    [InlineData("time without time zone", "time")]
    [InlineData("character varying", "text")]
    [InlineData("numeric", "numeric")]
    public void PostgreSql_validator_accepts_live_probed_bare_spellings(string probed, string expectedNormalized)
    {
        var (normalizedType, _) = PostgreSqlDdlTypeValidator.Validate(probed);

        normalizedType.Should().Be(expectedNormalized);
    }

    [Theory]
    [InlineData("varchar")]
    [InlineData("char")]
    public void MySql_validator_accepts_live_probed_bare_sized_string_spellings(string probed)
    {
        // MySQL's own DDL syntax has no length-less VARCHAR/CHAR at all (unlike Postgres's "character
        // varying") — text is the only safe, syntactically valid stand-in that can't truncate existing data.
        var (normalizedType, maxLength) = MySqlDdlTypeValidator.Validate(probed);

        normalizedType.Should().Be("text");
        maxLength.Should().BeNull();
    }
}
