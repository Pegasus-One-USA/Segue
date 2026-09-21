using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Covers <see cref="RelationalDestinationWriterBase.Stringify"/>, the value-shaping step every mapped record
/// value passes through before it's bound as an ADO parameter for a PostgreSQL/MySQL insert or update. Regression
/// guard for the "column is of type integer but expression is of type text" (Postgres 42804) bug: a genuinely
/// numeric CLR value (e.g. from <c>JsonMappingEngine.ConvertInteger</c> or <c>DateMathAgeNode</c>'s age
/// calculation) used to be stringified right back into text before it ever reached Npgsql, which PostgreSQL then
/// refused to implicitly cast back to the destination column's real numeric type.
/// </summary>
public sealed class RelationalDestinationWriterBaseTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(42L)]
    [InlineData((short)42)]
    public void Stringify_IntegerTypes_PassThroughNatively(object value)
    {
        RelationalDestinationWriterBase.Stringify(value).Should().Be(value).And.BeOfType(value.GetType());
    }

    [Fact]
    public void Stringify_Decimal_PassesThroughNatively()
    {
        var value = 12.34m;

        RelationalDestinationWriterBase.Stringify(value).Should().Be(value).And.BeOfType<decimal>();
    }

    [Fact]
    public void Stringify_Double_PassesThroughNatively()
    {
        var value = 12.34d;

        RelationalDestinationWriterBase.Stringify(value).Should().Be(value).And.BeOfType<double>();
    }

    [Fact]
    public void Stringify_Float_PassesThroughNatively()
    {
        var value = 12.34f;

        RelationalDestinationWriterBase.Stringify(value).Should().Be(value).And.BeOfType<float>();
    }

    [Fact]
    public void Stringify_Null_BecomesDbNull()
    {
        RelationalDestinationWriterBase.Stringify(null).Should().Be(DBNull.Value);
    }

    [Fact]
    public void Stringify_Bool_PassesThroughNatively()
    {
        RelationalDestinationWriterBase.Stringify(true).Should().Be(true).And.BeOfType<bool>();
    }

    [Fact]
    public void Stringify_DateTime_PassesThroughNatively()
    {
        var value = new DateTime(1990, 5, 15);

        RelationalDestinationWriterBase.Stringify(value).Should().Be(value).And.BeOfType<DateTime>();
    }

    [Fact]
    public void Stringify_DateOnly_FormatsAsIso8601Date()
    {
        var value = new DateOnly(1990, 5, 15);

        RelationalDestinationWriterBase.Stringify(value).Should().Be("1990-05-15");
    }

    [Fact]
    public void Stringify_DateTimeOffset_FormatsAsIso8601DateTime()
    {
        var value = new DateTimeOffset(1990, 5, 15, 8, 0, 0, TimeSpan.Zero);

        RelationalDestinationWriterBase.Stringify(value).Should().Be("1990-05-15 08:00:00");
    }

    // Anything that isn't one of the special-cased CLR types (a plain string, a Guid, an enum, ...) still falls
    // through to Convert.ToString — this is the fallback the numeric arms must NOT be reabsorbed into.
    [Fact]
    public void Stringify_UnhandledType_FallsBackToString()
    {
        var value = Guid.NewGuid();

        RelationalDestinationWriterBase.Stringify(value).Should().Be(value.ToString());
    }
}
