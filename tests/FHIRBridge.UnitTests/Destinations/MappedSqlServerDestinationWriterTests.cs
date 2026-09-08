using System.Data;
using System.Data.SqlTypes;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Covers <see cref="MappedSqlServerDestinationWriter.AddColumnParameter"/>, the date-safe parameter binder.
/// Regression guard for the "SqlDateTime overflow" that failed a whole batch when a single record carried a
/// pre-1753 date (e.g. a historical birth date) or a default/sentinel <see cref="DateTime.MinValue"/> — because
/// <c>AddWithValue</c> infers the legacy <c>datetime</c> type (range 1753-9999) and overflows while serializing
/// the parameter, before it ever reaches the column.
/// </summary>
public sealed class MappedSqlServerDestinationWriterTests
{
    private static SqlParameter Bind(object? value)
    {
        using var command = new SqlCommand();
        MappedSqlServerDestinationWriter.AddColumnParameter(command, "@p", value);
        return command.Parameters["@p"];
    }

    [Fact]
    public void AddColumnParameter_PreSqlDateTimeRangeDate_BindsAsDateTime2_WithoutOverflow()
    {
        // A genuine pre-1753 birth date — this is exactly what the legacy datetime inference could not serialize.
        var historicalDob = new DateTime(1212, 12, 4);

        // Sanity: proves the value really is outside the legacy datetime range (the root cause we are guarding).
        var overflow = Record.Exception(() => _ = new SqlDateTime(historicalDob));
        overflow.Should().BeOfType<SqlTypeException>();

        var parameter = Bind(historicalDob);

        parameter.SqlDbType.Should().Be(SqlDbType.DateTime2);
        parameter.Value.Should().Be(historicalDob);
    }

    [Fact]
    public void AddColumnParameter_NormalDate_BindsAsDateTime2()
    {
        var dob = new DateTime(1990, 5, 15);

        var parameter = Bind(dob);

        parameter.SqlDbType.Should().Be(SqlDbType.DateTime2);
        parameter.Value.Should().Be(dob);
    }

    [Fact]
    public void AddColumnParameter_DefaultDate_IsWrittenAsNull()
    {
        // A "no value" sentinel must become NULL, not stored as year 0001.
        var parameter = Bind(default(DateTime));

        parameter.Value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void AddColumnParameter_NormalDateTimeOffset_BindsAsDateTimeOffset()
    {
        var value = new DateTimeOffset(1990, 5, 15, 8, 0, 0, TimeSpan.Zero);

        var parameter = Bind(value);

        parameter.SqlDbType.Should().Be(SqlDbType.DateTimeOffset);
        parameter.Value.Should().Be(value);
    }

    [Fact]
    public void AddColumnParameter_DefaultDateTimeOffset_IsWrittenAsNull()
    {
        var parameter = Bind(default(DateTimeOffset));

        parameter.Value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void AddColumnParameter_Null_IsWrittenAsNull()
    {
        var parameter = Bind(null);

        parameter.Value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void AddColumnParameter_NonDateValue_IsBoundUnchanged()
    {
        var parameter = Bind("Smith");

        parameter.Value.Should().Be("Smith");
    }
}
