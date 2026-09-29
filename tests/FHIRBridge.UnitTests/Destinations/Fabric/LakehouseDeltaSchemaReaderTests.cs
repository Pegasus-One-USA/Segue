using FHIRBridge.Infrastructure.Destinations.Fabric;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Covers reading a Delta table's columns back out of its transaction log — the half of table discovery that is
/// pure parsing. Listing itself needs a real OneLake container (the discovery rule is "a folder under Tables/
/// containing a _delta_log"), so it is exercised against a tenant rather than against a mocked blob client that
/// would only confirm this test's own assumptions.
///
/// <para>These deliberately round-trip against <see cref="DeltaTransactionLog.BuildSchemaString"/>: the writer
/// and the reader have to agree about the same format, and asserting the reader alone would let them drift
/// apart while both kept passing.</para>
/// </summary>
public sealed class LakehouseDeltaSchemaReaderTests
{
    /// <summary>
    /// The writer's own output must be readable by the reader. This is the case that matters most: every table
    /// FHIRBridge creates is described by a schemaString this codebase wrote.
    /// </summary>
    [Fact]
    public void A_schema_this_codebase_wrote_reads_back_with_the_same_columns()
    {
        var schemaString = DeltaTransactionLog.BuildSchemaString(["Id", "GivenName", "LastName"]);

        var columns = LakehouseDeltaSchemaReader.ParseSchemaString(schemaString);

        columns.Should().NotBeNull();
        columns!.Select(column => column.Name).Should().Equal("Id", "GivenName", "LastName");
        columns.Should().OnlyContain(column => column.DataType == "string" && column.IsNullable);
    }

    /// <summary>
    /// A table written by Spark or another Delta writer carries real types, so the reader must handle more than
    /// the all-string schema this codebase emits — otherwise the picker would misdescribe every table it did
    /// not create itself.
    /// </summary>
    [Theory]
    [InlineData("string", "String")]
    [InlineData("boolean", "Boolean")]
    [InlineData("integer", "Integer")]
    [InlineData("long", "Integer")]
    [InlineData("double", "Decimal")]
    [InlineData("decimal(10,2)", "Decimal")]
    [InlineData("date", "Date")]
    [InlineData("timestamp", "DateTime")]
    public void Delta_types_map_to_the_canvas_value_types(string deltaType, string expected)
        => LakehouseDeltaSchemaReader.MapValueType(deltaType).Should().Be(expected);

    /// <summary>
    /// An unrecognized type falls back to String rather than being dropped or throwing: the column genuinely
    /// exists, and String is what the writer would emit for it anyway.
    /// </summary>
    [Fact]
    public void An_unknown_type_falls_back_to_string()
        => LakehouseDeltaSchemaReader.MapValueType("interval").Should().Be("String");

    /// <summary>
    /// Nullability is read from the field, not assumed. A non-nullable column matters to the canvas, which uses
    /// it to warn about an unmapped required field.
    /// </summary>
    [Fact]
    public void Nullability_is_read_from_the_field()
    {
        var columns = LakehouseDeltaSchemaReader.ParseSchemaString(
            """
            {"type":"struct","fields":[
              {"name":"Id","type":"string","nullable":false,"metadata":{}},
              {"name":"GivenName","type":"string","nullable":true,"metadata":{}}]}
            """);

        columns.Should().NotBeNull();
        columns![0].IsNullable.Should().BeFalse();
        columns[1].IsNullable.Should().BeTrue();
    }

    /// <summary>
    /// A nested column (struct/array/map) has an OBJECT where a primitive has a type name. It is reported by its
    /// shape rather than skipped — hiding it would make the picker disagree with the table, and a user looking
    /// for a column they can see in Fabric would not find it.
    /// </summary>
    [Fact]
    public void A_nested_column_is_reported_rather_than_skipped()
    {
        var columns = LakehouseDeltaSchemaReader.ParseSchemaString(
            """
            {"type":"struct","fields":[
              {"name":"Name","type":{"type":"struct","fields":[]},"nullable":true,"metadata":{}},
              {"name":"Id","type":"string","nullable":true,"metadata":{}}]}
            """);

        columns.Should().NotBeNull();
        columns!.Select(column => column.Name).Should().Equal("Name", "Id");
        columns[0].DataType.Should().Be("struct");
        columns[0].MappingValueType.Should().Be("String");
    }

    /// <summary>
    /// A Delta table has no primary key, no uniqueness constraint and no generated columns of the kind this DTO
    /// means. Those stay false rather than being guessed from a column called "Id" — the relational writers
    /// learned that lesson (see DestinationColumnSchemaDto.IsPrimaryKey's own note about never guessing).
    /// </summary>
    [Fact]
    public void Relational_only_column_flags_are_never_guessed()
    {
        var columns = LakehouseDeltaSchemaReader.ParseSchemaString(
            DeltaTransactionLog.BuildSchemaString(["Id"]));

        columns.Should().NotBeNull();
        columns![0].IsPrimaryKey.Should().BeFalse();
        columns[0].IsUnique.Should().BeFalse();
        columns[0].IsAutoGenerated.Should().BeFalse();
        columns[0].IsForeignKey.Should().BeFalse();
    }

    /// <summary>
    /// Malformed input returns null rather than throwing, so one unreadable log costs a table its columns and
    /// not the whole picker.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_returns_null(string? schemaString)
        => LakehouseDeltaSchemaReader.ParseSchemaString(schemaString).Should().BeNull();

    /// <summary>
    /// Valid JSON that is not a Delta schema (no fields array) is also null — a struct is what the protocol
    /// specifies, and anything else is not a schema this can describe.
    /// </summary>
    [Fact]
    public void Json_without_a_fields_array_returns_null()
        => LakehouseDeltaSchemaReader.ParseSchemaString("""{"type":"struct"}""").Should().BeNull();

    /// <summary>
    /// A field with no name cannot be a column, so it is skipped while its well-formed siblings are kept —
    /// dropping the whole table for one bad entry would hide a table the user can see in Fabric.
    /// </summary>
    [Fact]
    public void A_nameless_field_is_skipped_without_losing_the_others()
    {
        var columns = LakehouseDeltaSchemaReader.ParseSchemaString(
            """
            {"type":"struct","fields":[
              {"type":"string","nullable":true,"metadata":{}},
              {"name":"Id","type":"string","nullable":true,"metadata":{}}]}
            """);

        columns.Should().NotBeNull();
        columns!.Select(column => column.Name).Should().Equal("Id");
    }
}
