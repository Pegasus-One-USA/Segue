using System.Text.Json;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Covers the <c>_delta_log</c> entries against the Delta Transaction Log Protocol
/// (https://github.com/delta-io/delta/blob/master/PROTOCOL.md).
///
/// <para>These assert the wire format rather than round-tripping through a Delta reader, because the whole point
/// of writing the log by hand is that no Delta library is referenced. That makes the protocol the specification
/// under test, so each case names the rule it is holding the writer to — a test that only restated the
/// implementation would pass just as happily against a log no Fabric table could read.</para>
/// </summary>
public sealed class DeltaTransactionLogTests
{
    private static readonly IReadOnlyList<string> Columns = ["Id", "GivenName", "LastName"];

    private static readonly IReadOnlyList<DeltaAddedFile> OneFile =
        [new DeltaAddedFile("part-00000-abc.parquet", 2048, 7)];

    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 25, 10, 30, 0, TimeSpan.Zero);

    private static List<JsonElement> ParseActions(string commit)
        => commit
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

    private static JsonElement Action(string commit, string actionName)
        => ParseActions(commit).Single(element => element.TryGetProperty(actionName, out _))
            .GetProperty(actionName);

    /// <summary>
    /// Protocol §Delta Log Entries: "named using the next available version number, zero-padded to 20 digits".
    /// A reader finds the log by sorting these names, so a shorter name sorts wrongly once a table passes ten
    /// commits — the kind of fault that appears only after a table has been in use for a while.
    /// </summary>
    [Theory]
    [InlineData(0, "00000000000000000000.json")]
    [InlineData(1, "00000000000000000001.json")]
    [InlineData(42, "00000000000000000042.json")]
    [InlineData(1234567890, "00000000001234567890.json")]
    public void Commit_files_are_named_by_version_zero_padded_to_twenty_digits(long version, string expected)
        => DeltaTransactionLog.CommitFileName(version).Should().Be(expected);

    /// <summary>
    /// Protocol §Delta Log Entries: the log is newline-delimited JSON, "where every action is stored as a
    /// single-line JSON document". A pretty-printed action breaks a reader parsing line by line.
    /// </summary>
    [Fact]
    public void Every_action_is_one_line_of_json()
    {
        var commit = DeltaTransactionLog.BuildInitialCommit(
            Guid.NewGuid(), "Patient", Columns, OneFile, Timestamp);

        commit.Should().EndWith("\n");
        commit.TrimEnd('\n').Split('\n').Should().OnlyContain(line => !line.Contains('\n') && line.Trim().Length > 0);

        // Parses as NDJSON: protocol, metaData, add.
        ParseActions(commit).Should().HaveCount(3);
    }

    /// <summary>
    /// Protocol §Change Metadata: "The first version of a table must contain a metaData action." Without it a
    /// reader has no schema and the folder never registers as a table — the exact failure this whole strategy
    /// exists to avoid.
    /// </summary>
    [Fact]
    public void The_first_commit_carries_protocol_and_metadata_and_the_add()
    {
        var commit = DeltaTransactionLog.BuildInitialCommit(
            Guid.NewGuid(), "Patient", Columns, OneFile, Timestamp);

        var actions = ParseActions(commit);
        actions.Should().SatisfyRespectively(
            first => first.TryGetProperty("protocol", out _).Should().BeTrue(),
            second => second.TryGetProperty("metaData", out _).Should().BeTrue(),
            third => third.TryGetProperty("add", out _).Should().BeTrue());
    }

    /// <summary>
    /// Reader 1 / writer 2 is the plain featureless table. Claiming a higher version would exclude readers for
    /// features this writer does not use.
    /// </summary>
    [Fact]
    public void The_protocol_action_claims_the_lowest_versions_that_support_what_is_written()
    {
        var protocol = Action(
            DeltaTransactionLog.BuildInitialCommit(Guid.NewGuid(), "Patient", Columns, OneFile, Timestamp),
            "protocol");

        protocol.GetProperty("minReaderVersion").GetInt32().Should().Be(1);
        protocol.GetProperty("minWriterVersion").GetInt32().Should().Be(2);

        // readerFeatures/writerFeatures exist only at reader 3 / writer 7. Emitting them here would be invalid.
        protocol.TryGetProperty("readerFeatures", out _).Should().BeFalse();
        protocol.TryGetProperty("writerFeatures", out _).Should().BeFalse();
    }

    /// <summary>
    /// Protocol §Change Metadata: schemaString is a Schema Struct serialized to a STRING. Emitting it as a nested
    /// JSON object is the single easiest mistake in the format, and it produces a log every Delta reader rejects —
    /// so this asserts the JSON kind, not just the content.
    /// </summary>
    [Fact]
    public void SchemaString_is_a_json_string_not_a_nested_object()
    {
        var metaData = Action(
            DeltaTransactionLog.BuildInitialCommit(Guid.NewGuid(), "Patient", Columns, OneFile, Timestamp),
            "metaData");

        var schemaString = metaData.GetProperty("schemaString");
        schemaString.ValueKind.Should().Be(JsonValueKind.String);

        var schema = JsonDocument.Parse(schemaString.GetString()!).RootElement;
        schema.GetProperty("type").GetString().Should().Be("struct");
        schema.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString())
            .Should().Equal("Id", "GivenName", "LastName");
    }

    /// <summary>
    /// Every column is a nullable string, matching what MappedDestinationParquetSerializer actually writes. A
    /// narrower declared type would be a claim the Parquet does not back up.
    /// </summary>
    [Fact]
    public void Every_column_is_declared_as_a_nullable_string()
    {
        var schema = JsonDocument.Parse(DeltaTransactionLog.BuildSchemaString(Columns)).RootElement;

        foreach (var field in schema.GetProperty("fields").EnumerateArray())
        {
            field.GetProperty("type").GetString().Should().Be("string");
            field.GetProperty("nullable").GetBoolean().Should().BeTrue();
            field.GetProperty("metadata").ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    /// <summary>
    /// Protocol §Change Metadata lists id, format, schemaString, partitionColumns and configuration as required.
    /// partitionColumns and configuration are required even when empty — absent is not the same as empty.
    /// </summary>
    [Fact]
    public void The_metadata_action_carries_every_required_field()
    {
        var tableId = Guid.NewGuid();
        var metaData = Action(
            DeltaTransactionLog.BuildInitialCommit(tableId, "Patient", Columns, OneFile, Timestamp),
            "metaData");

        metaData.GetProperty("id").GetString().Should().Be(tableId.ToString());
        metaData.GetProperty("format").GetProperty("provider").GetString().Should().Be("parquet");
        metaData.GetProperty("partitionColumns").ValueKind.Should().Be(JsonValueKind.Array);
        metaData.GetProperty("partitionColumns").GetArrayLength().Should().Be(0);
        metaData.GetProperty("configuration").ValueKind.Should().Be(JsonValueKind.Object);
        metaData.GetProperty("createdTime").GetInt64().Should().Be(Timestamp.ToUnixTimeMilliseconds());
        metaData.GetProperty("name").GetString().Should().Be("Patient");
    }

    /// <summary>
    /// Protocol §Add File: path, partitionValues, size, modificationTime and dataChange are all required.
    /// partitionValues is required even on an unpartitioned table, where it is an empty map.
    /// </summary>
    [Fact]
    public void The_add_action_carries_every_required_field()
    {
        var add = Action(
            DeltaTransactionLog.BuildInitialCommit(Guid.NewGuid(), "Patient", Columns, OneFile, Timestamp),
            "add");

        add.GetProperty("path").GetString().Should().Be("part-00000-abc.parquet");
        add.GetProperty("partitionValues").ValueKind.Should().Be(JsonValueKind.Object);
        add.GetProperty("size").GetInt64().Should().Be(2048);
        add.GetProperty("modificationTime").GetInt64().Should().Be(Timestamp.ToUnixTimeMilliseconds());
        add.GetProperty("dataChange").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// The add action's path is relative to the table root, so the table survives being moved or mounted at a
    /// different path. An absolute URL here would pin the table to the workspace it was written in.
    /// </summary>
    [Fact]
    public void The_add_path_is_relative_to_the_table_root()
    {
        var add = Action(
            DeltaTransactionLog.BuildInitialCommit(Guid.NewGuid(), "Patient", Columns, OneFile, Timestamp),
            "add");

        var path = add.GetProperty("path").GetString()!;
        path.Should().NotStartWith("/").And.NotContain("://").And.NotContain("Tables/");
    }

    /// <summary>
    /// stats is optional, but numRecords is what lets a reader answer COUNT(*) without opening the Parquet. Like
    /// schemaString it is a serialized STRING, not a nested object.
    /// </summary>
    [Fact]
    public void Stats_report_the_row_count_as_a_serialized_string()
    {
        var add = Action(
            DeltaTransactionLog.BuildInitialCommit(Guid.NewGuid(), "Patient", Columns, OneFile, Timestamp),
            "add");

        var stats = add.GetProperty("stats");
        stats.ValueKind.Should().Be(JsonValueKind.String);
        JsonDocument.Parse(stats.GetString()!).RootElement.GetProperty("numRecords").GetInt64().Should().Be(7);
    }

    /// <summary>
    /// A later commit adds files only. Re-stating metaData would overwrite the table's current metadata wholesale
    /// rather than merge, so an append that repeated it could silently reset table configuration.
    /// </summary>
    [Fact]
    public void A_later_commit_carries_only_add_actions()
    {
        var commit = DeltaTransactionLog.BuildAppendCommit(OneFile, Timestamp);

        var actions = ParseActions(commit);
        actions.Should().ContainSingle();
        actions[0].TryGetProperty("add", out _).Should().BeTrue();
        commit.Should().NotContain("metaData").And.NotContain("protocol");
    }

    /// <summary>
    /// A commit can carry several files — one batch could be split across files in future without the log format
    /// changing — and each gets its own add action.
    /// </summary>
    [Fact]
    public void Each_added_file_gets_its_own_add_action()
    {
        var commit = DeltaTransactionLog.BuildAppendCommit(
            [
                new DeltaAddedFile("part-a.parquet", 10, 1),
                new DeltaAddedFile("part-b.parquet", 20, 2),
            ],
            Timestamp);

        ParseActions(commit).Select(action => action.GetProperty("add").GetProperty("path").GetString())
            .Should().Equal("part-a.parquet", "part-b.parquet");
    }

    /// <summary>
    /// An unnamed table omits the optional name field rather than emitting null — the protocol distinguishes
    /// absent from null, and a reader is entitled to treat an explicit null as a name of null.
    /// </summary>
    [Fact]
    public void An_unnamed_table_omits_the_optional_name_field()
    {
        var metaData = Action(
            DeltaTransactionLog.BuildInitialCommit(Guid.NewGuid(), null, Columns, OneFile, Timestamp),
            "metaData");

        metaData.TryGetProperty("name", out _).Should().BeFalse();
    }
}
