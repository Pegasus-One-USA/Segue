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

/// <summary>
/// A reference target (table + key column) comes from the mapping PROFILE, so a typo in one breaks every record
/// identically — a configuration fault, not a data fault. Validated inside the per-record loop it was caught by
/// that loop's isolation and reported as "N of N records failed to write", which reads as bad data and sends the
/// operator looking in the wrong place. It has to fail before the loop, with its own message.
/// </summary>
public sealed class ReferenceLookupTargetValidationTests
{
    private static MappedDestinationRecord Record(params MappedReferenceLookup[] lookups) =>
        new(Guid.NewGuid(), "Observation", "dbo.Observation", "o1", new Dictionary<string, object?>(),
            ReferenceLookups: lookups);

    [Fact]
    public void A_malformed_key_column_fails_before_any_record_is_processed()
    {
        var act = () => MappedSqlServerDestinationWriter.ValidateReferenceLookupTargets(
            [Record(new MappedReferenceLookup("PatientId", "Patient", "Patient Id; DROP TABLE x", "p-1"))]);

        act.Should().Throw<Exception>("a broken profile must fail the route, not be isolated as a record error");
    }

    [Fact]
    public void A_well_formed_batch_passes()
    {
        var act = () => MappedSqlServerDestinationWriter.ValidateReferenceLookupTargets(
        [
            Record(new MappedReferenceLookup("PatientId", "dbo.Patient", "PatientId", "p-1")),
            Record(new MappedReferenceLookup("EncounterId", "Encounter", "EncounterId", "e-1")),
        ]);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_lookup_with_no_reference_id_is_not_validated()
    {
        // Nothing to resolve, so the target is never used — validating it would fail a batch that works.
        var act = () => MappedSqlServerDestinationWriter.ValidateReferenceLookupTargets(
            [Record(new MappedReferenceLookup("PatientId", "Patient", "bad column!", null))]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Records_with_no_lookups_at_all_pass()
    {
        var act = () => MappedSqlServerDestinationWriter.ValidateReferenceLookupTargets([Record()]);

        act.Should().NotThrow();
    }
}

/// <summary>
/// The unresolved-reference message shortens the id it could not find, because that message is now retained in
/// the run's output rather than being a transient exception. Shortening must not destroy the discrimination the
/// message exists for: a pseudonymised id is a constant marker plus a hash, so counting the marker against the
/// budget left three varying characters — 4096 possible strings — and every de-identified id rendered alike.
/// </summary>
public sealed class ReferenceIdAbbreviationTests
{
    [Fact]
    public void Two_different_pseudonyms_do_not_render_identically()
    {
        // The 16-hex shape SafeHarborDeIdentificationService.Hash produces, differing only after the marker.
        var first = MappedSqlServerDestinationWriter.Abbreviate("anon-20c5269a14d38c60");
        var second = MappedSqlServerDestinationWriter.Abbreviate("anon-625e79d324a4ff7b");

        first.Should().NotBe(second, "telling one missing reference from another is the whole point");
        first.Should().StartWith("anon-", "the marker says this is de-identified data, not a raw id");
        first.Should().Contain("20c5269a", "the varying part is what identifies which row is missing");
    }

    [Fact]
    public void A_raw_id_is_still_cut_to_the_same_budget()
    {
        // No marker to preserve, so nothing changes for a real identifier — the privacy-relevant case.
        var value = MappedSqlServerDestinationWriter.Abbreviate("e63wRTbPfr1p8UW81d8Seiw3");

        value.Should().StartWith("e63wRTbP");
        value.Should().NotContain("1d8Seiw3", "a raw identifier must not be reproduced in full");
        value.Should().HaveLength(9, "eight characters plus the ellipsis");
    }

    [Fact]
    public void A_pseudonym_never_reproduces_its_whole_hash()
    {
        MappedSqlServerDestinationWriter.Abbreviate("anon-20c5269a14d38c60")
            .Should().NotContain("14d38c60", "the marker is free, the hash still is not");
    }

    [Theory]
    [InlineData("p-1")]
    [InlineData("anon-1234")]
    [InlineData("")]
    public void A_value_no_longer_than_the_budget_is_left_alone(string referenceId)
    {
        MappedSqlServerDestinationWriter.Abbreviate(referenceId).Should().Be(referenceId);
    }

    [Fact]
    public void A_null_id_renders_as_nothing_rather_than_throwing()
    {
        MappedSqlServerDestinationWriter.Abbreviate(null).Should().BeEmpty();
    }
}
