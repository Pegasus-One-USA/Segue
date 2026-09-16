using System.Data;
using System.Data.SqlTypes;
using FHIRBridge.Application.DTOs;
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

/// <summary>
/// Reference resolution ran unguarded ahead of the per-record write path, so ONE record whose reference matched
/// no row threw out of WriteAsync and discarded the entire batch — every other record and every resource type
/// written after it, none of which had anything wrong. The writer explicitly promises the opposite ("each
/// record's write stands alone"), and the trigger is routine: a child referencing a parent the source never
/// delivered, e.g. an Observation pointing at an Encounter outside the fetched set.
/// </summary>
public sealed class ReferenceLookupIsolationTests
{
    private static MappedDestinationRecord Record(string resourceType, string id) =>
        new(Guid.NewGuid(), resourceType, $"dbo.{resourceType}", id, new Dictionary<string, object?>());

    /// <summary>Mimics ResolveReferenceLookupsAsync: throws InvalidOperationException for an unresolvable one.</summary>
    private static Func<MappedDestinationRecord, CancellationToken, Task<MappedDestinationRecord>> ResolverFailingOn(
        params string[] failingIds) =>
        (record, _) => failingIds.Contains(record.SourceResourceId)
            ? throw new InvalidOperationException(
                $"Cannot resolve 'EncounterId': no row in [dbo].[Encounter] has [EncounterId] = '{record.SourceResourceId}'.")
            : Task.FromResult(record);

    [Fact]
    public async Task One_unresolvable_reference_does_not_discard_the_rest_of_the_batch()
    {
        var records = new[] { Record("Observation", "o1"), Record("Observation", "o2"), Record("Observation", "o3") };
        var errors = new List<string>();

        var resolved = await MappedSqlServerDestinationWriter.ResolveReferenceLookupsIsolatedAsync(
            records, ResolverFailingOn("o2"), errors, CancellationToken.None);

        resolved.Select(r => r.SourceResourceId).Should().Equal("o1", "o3");
        errors.Should().ContainSingle().Which.Should().Contain("o2");
    }

    [Fact]
    public async Task The_reported_error_names_the_record_that_failed()
    {
        var errors = new List<string>();

        await MappedSqlServerDestinationWriter.ResolveReferenceLookupsIsolatedAsync(
            [Record("Observation", "o1")], ResolverFailingOn("o1"), errors, CancellationToken.None);

        errors.Should().ContainSingle().Which.Should()
            .StartWith("Observation/o1: ", "an isolated failure is useless without saying which record it was")
            .And.Contain("no row in [dbo].[Encounter]", "the underlying cause must survive");
    }

    [Fact]
    public async Task A_batch_whose_references_all_resolve_reports_nothing_and_keeps_every_record()
    {
        var records = new[] { Record("Observation", "o1"), Record("Observation", "o2") };
        var errors = new List<string>();

        var resolved = await MappedSqlServerDestinationWriter.ResolveReferenceLookupsIsolatedAsync(
            records, ResolverFailingOn(), errors, CancellationToken.None);

        resolved.Should().HaveCount(2);
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_that_is_not_an_unresolved_reference_still_fails_the_route()
    {
        // The connection dying cannot be recovered from per record — it must propagate, as it did before.
        var errors = new List<string>();

        var act = async () => await MappedSqlServerDestinationWriter.ResolveReferenceLookupsIsolatedAsync(
            [Record("Observation", "o1")],
            (_, _) => throw new TimeoutException("connection lost"),
            errors, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        errors.Should().BeEmpty();
    }
}
