using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Covers the two actual SQL-injection boundaries for the mapping canvas's real DDL execution
/// (ALTER TABLE ADD/DROP/ALTER COLUMN, CREATE TABLE): the identifier allowlist and the data-type allowlist,
/// for SQL Server/Azure SQL, MySQL, and PostgreSQL alike (schema mutation used to be SQL Server/Azure SQL
/// only — see "Implement MySQL and PostgreSQL schema mutation support" — this file covers all three now).
/// Both boundaries are exercised through the public service so a rejection is proven to short-circuit
/// before any connection is attempted — no live database needed, since a malformed identifier/type is
/// rejected before <c>BuildConnectionString</c>/<c>OpenConnectionAsync</c> ever run. For a VALID input,
/// these tests can only prove the code reaches the connection stage and fails there (no live SQL Server/
/// MySQL/PostgreSQL server is reachable in this sandbox for any of the three) — never that the DDL
/// actually succeeds against a real database; see the manual Docker Compose smoke test for that.
/// </summary>
public sealed class SqlDestinationSchemaServiceTests
{
    private readonly SqlDestinationSchemaService _sut = new(
        Mock.Of<IConfigurationRepository>(),
        Mock.Of<ISecretProvider>());

    private static DestinationConnectionProbeRequest SqlServerConnection() => new(
        DestinationType.SqlServer, Server: "localhost", Database: "FHIRBridge");

    private static DestinationConnectionProbeRequest MySqlConnection() => new(
        DestinationType.MySql, Server: "localhost", Database: "fhirbridge_output");

    private static DestinationConnectionProbeRequest PostgreSqlConnection() => new(
        DestinationType.PostgreSql, Server: "localhost", Database: "fhirbridge_output");

    // ── SQL Server / Azure SQL — regression: every one of these already passed before this change ──────

    [Theory]
    [InlineData("dbo.PatientContact; DROP TABLE Users")]
    [InlineData("dbo.Patient-Contact")]
    [InlineData("1dbo.Patient")]
    [InlineData("a.b.c")]
    public async Task AddColumnAsync_SqlServer_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(SqlServerConnection(), tableName, "Notes", "nvarchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Notes]; DROP TABLE Users; --")]
    [InlineData("Notes Field")]
    [InlineData("1Notes")]
    public async Task AddColumnAsync_SqlServer_rejects_a_malformed_column_name(string columnName)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(SqlServerConnection(), "dbo.PatientContact", columnName, "nvarchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("nvarchar(50); DROP TABLE Users")]
    [InlineData("EXEC xp_cmdshell")]
    [InlineData("nvarchar(5000)")] // over the 4000 cap
    [InlineData("decimal(50,2)")] // precision over 38
    [InlineData("decimal(10,20)")] // scale exceeds precision
    [InlineData("varchar(0)")]
    public async Task AddColumnAsync_SqlServer_rejects_a_disallowed_data_type(string dataType)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(SqlServerConnection(), "dbo.PatientContact", "Notes", dataType),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("int")]
    [InlineData("BIGINT")]
    [InlineData("bit")]
    [InlineData("date")]
    [InlineData("datetime2")]
    [InlineData("uniqueidentifier")]
    [InlineData("float")]
    [InlineData("real")]
    [InlineData("nvarchar(50)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("varchar(255)")]
    [InlineData("decimal(18,2)")]
    [InlineData("numeric(10,0)")]
    public async Task AddColumnAsync_SqlServer_accepts_an_allowed_data_type_and_fails_only_at_the_connection_stage(
        string dataType)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(SqlServerConnection(), "dbo.PatientContact", "Notes", dataType),
            CancellationToken.None);

        // A real connection will fail against "localhost" in a unit-test sandbox, but that failure must
        // come from the connection attempt, never from data-type validation rejecting a valid type.
        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
        result.Error.Should().NotContain("must be between 1 and 4000");
        result.Error.Should().NotContain("precision/scale");
    }

    [Theory]
    [InlineData("dbo.NewTable; DROP TABLE Users")]
    [InlineData("a.b.c")]
    [InlineData("1NewTable")]
    public async Task CreateTableAsync_SqlServer_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(SqlServerConnection(), tableName),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        // AlreadyExisted is CreateTableAsync's own "the live existence check found it, but that's a
        // success, not a failure" signal (see its doc comment) — a request rejected before any connection
        // is even attempted must never be confused for that outcome.
        result.AlreadyExisted.Should().BeFalse();
    }

    [Fact]
    public async Task CreateTableAsync_SqlServer_defaults_AlreadyExisted_to_false_on_an_ordinary_failure()
    {
        // Mirrors AddColumnAsync's own "accepts an allowed data type" test above — a real connection to
        // "localhost" fails in a unit-test sandbox, and that ordinary connection failure must never be
        // reported as the table having "already existed" (see SchemaMutationResultDto's own doc comment on
        // AlreadyExisted: it is set ONLY when a live TableExistsAsync check actually ran and found the
        // table, which requires a real connection this test deliberately never reaches).
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(SqlServerConnection(), "dbo.NewTable"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.AlreadyExisted.Should().BeFalse();
    }

    [Theory]
    [InlineData("dbo.PatientContact; DROP TABLE Users")]
    [InlineData("dbo.Patient-Contact")]
    [InlineData("1dbo.Patient")]
    [InlineData("a.b.c")]
    public async Task DropColumnAsync_SqlServer_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(SqlServerConnection(), tableName, "Notes"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Notes]; DROP TABLE Users; --")]
    [InlineData("Notes Field")]
    [InlineData("1Notes")]
    public async Task DropColumnAsync_SqlServer_rejects_a_malformed_column_name(string columnName)
    {
        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(SqlServerConnection(), "dbo.PatientContact", columnName),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("dbo.PatientContact; DROP TABLE Users")]
    [InlineData("a.b.c")]
    public async Task AlterColumnAsync_SqlServer_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.AlterColumnAsync(
            new AlterColumnRequest(SqlServerConnection(), tableName, "Notes", "nvarchar(100)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AlterColumnAsync_SqlServer_rejects_a_disallowed_new_data_type()
    {
        var result = await _sut.AlterColumnAsync(
            new AlterColumnRequest(SqlServerConnection(), "dbo.PatientContact", "Notes", "decimal(50,2)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("precision/scale is out of range");
    }

    [Fact]
    public async Task AlterColumnAsync_SqlServer_accepts_an_allowed_data_type_and_fails_only_at_the_connection_stage()
    {
        var result = await _sut.AlterColumnAsync(
            new AlterColumnRequest(SqlServerConnection(), "dbo.PatientContact", "Notes", "varchar(100)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
    }

    // ── genuinely non-relational destinations are still rejected up front, for every mutation method ────

    [Theory]
    [InlineData(DestinationType.Mongo)]
    [InlineData(DestinationType.Csv)]
    public async Task AddColumnAsync_rejects_non_relational_destination_types_up_front(DestinationType type)
    {
        var connection = SqlServerConnection() with { DestinationType = type };

        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(connection, "dbo.PatientContact", "Notes", "nvarchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support column creation");
    }

    [Fact]
    public async Task CreateTableAsync_rejects_non_relational_destination_types_up_front()
    {
        var connection = SqlServerConnection() with { DestinationType = DestinationType.Mongo };

        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(connection, "dbo.NewTable"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support table creation");
    }

    [Fact]
    public async Task DropColumnAsync_rejects_non_relational_destination_types_up_front()
    {
        var connection = SqlServerConnection() with { DestinationType = DestinationType.Mongo };

        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(connection, "dbo.PatientContact", "Notes"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support dropping columns");
    }

    [Fact]
    public async Task AlterColumnAsync_rejects_non_relational_destination_types_up_front()
    {
        var connection = SqlServerConnection() with { DestinationType = DestinationType.Mongo };

        var result = await _sut.AlterColumnAsync(
            new AlterColumnRequest(connection, "dbo.PatientContact", "Notes", "nvarchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support altering columns");
    }

    // ── MySQL — the exact end-to-end scenario the screenshot reported: "does not support table creation" ──

    [Theory]
    [InlineData("Patient; DROP TABLE Users")]
    [InlineData("Patient-Contact")]
    [InlineData("1Patient")]
    public async Task CreateTableAsync_MySql_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(MySqlConnection(), tableName),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.AlreadyExisted.Should().BeFalse();
    }

    [Theory]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("bit")] // -> Boolean (MySQL has no native boolean; kept as its own real BIT type)
    [InlineData("boolean")]
    [InlineData("date")]
    [InlineData("datetime2")] // -> normalized to MySQL's own "datetime"
    [InlineData("uniqueidentifier")] // -> normalized to "char(36)" (MySQL has no native UUID type)
    [InlineData("nvarchar(50)")] // -> normalized to "varchar(50)"
    [InlineData("nvarchar(max)")] // -> normalized to "text" (MySQL VARCHAR has no MAX form)
    [InlineData("varchar(255)")]
    [InlineData("decimal(18,2)")]
    public async Task CreateTableAsync_MySql_accepts_every_required_data_type_and_fails_only_at_the_connection_stage(
        string dataType)
    {
        // Covers the explicit mapping checklist (string/int/bigint/boolean/decimal/date/datetime/uuid/text)
        // for MySQL — none of these may be rejected as "not an allowed data type" the way they'd previously
        // have been (see MySqlDdlTypeValidator's own fix for "datetime2"/"uniqueidentifier", which used to
        // throw here before this change).
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(MySqlConnection(), "Patient", [new TableColumnDefinition("Notes", dataType)]),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
    }

    [Fact]
    public async Task CreateTableAsync_MySql_reaches_the_connection_stage_for_a_bare_table_name()
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(MySqlConnection(), "Patient"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.AlreadyExisted.Should().BeFalse();
    }

    [Fact]
    public async Task CreateTableAsync_MySql_reaches_the_connection_stage_for_a_database_qualified_table_name()
    {
        // The live schema probe reports an existing MySQL table's fullName as "{database}.Patient" (see
        // ReadColumnsAsync) — SplitTableName must accept and correctly parse this shape too (discarding the
        // database-name prefix, since MySQL has no schema layer distinct from the database), not just a
        // bare name, or re-selecting an already-confirmed table for "Add column" would be rejected as
        // malformed.
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(MySqlConnection(), "fhirbridge_output.Patient"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.AlreadyExisted.Should().BeFalse();
    }

    [Theory]
    [InlineData("Notes]; DROP TABLE Users; --")]
    [InlineData("Notes Field")]
    [InlineData("1Notes")]
    public async Task AddColumnAsync_MySql_rejects_a_malformed_column_name(string columnName)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(MySqlConnection(), "Patient", columnName, "varchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AddColumnAsync_MySql_reaches_the_connection_stage_for_a_valid_request()
    {
        // Covers both "add a column to an existing table" and "add a column to a table already known by
        // its database-qualified fullName" — same request shape either way once past validation.
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(MySqlConnection(), "fhirbridge_output.Patient", "Notes", "varchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
    }

    [Fact]
    public async Task AlterColumnAsync_MySql_reaches_the_connection_stage_for_a_valid_request()
    {
        var result = await _sut.AlterColumnAsync(
            new AlterColumnRequest(MySqlConnection(), "Patient", "Notes", "varchar(100)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
    }

    [Fact]
    public async Task DropColumnAsync_MySql_reaches_the_connection_stage_for_a_valid_request()
    {
        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(MySqlConnection(), "Patient", "Notes"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateTableAsync_MySql_invalid_connection_fails_before_any_data_type_error()
    {
        // "Invalid connection" — Server/Database missing entirely, same requirement BuildMySqlConnectionString
        // already enforces; must fail with a connection-shaped message, not a validation one.
        var connection = new DestinationConnectionProbeRequest(DestinationType.MySql);

        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(connection, "Patient"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Server and Database are required");
    }

    // ── PostgreSQL — same shape, different default schema ("public" instead of "dbo") ──────────────────

    [Theory]
    [InlineData("Patient; DROP TABLE Users")]
    [InlineData("Patient-Contact")]
    [InlineData("1Patient")]
    [InlineData("a.b.c")]
    public async Task CreateTableAsync_PostgreSql_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(PostgreSqlConnection(), tableName),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.AlreadyExisted.Should().BeFalse();
    }

    [Theory]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("bit")] // -> "boolean" (PostgreSQL's BIT type means something else entirely)
    [InlineData("boolean")]
    [InlineData("date")]
    [InlineData("datetime2")] // -> normalized to "timestamp"
    [InlineData("uniqueidentifier")] // -> normalized to PostgreSQL's native "uuid"
    [InlineData("nvarchar(50)")] // -> normalized to "varchar(50)"
    [InlineData("nvarchar(max)")] // -> normalized to "text"
    [InlineData("varchar(255)")]
    [InlineData("decimal(18,2)")]
    public async Task CreateTableAsync_PostgreSql_accepts_every_required_data_type_and_fails_only_at_the_connection_stage(
        string dataType)
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(PostgreSqlConnection(), "Patient", [new TableColumnDefinition("Notes", dataType)]),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
    }

    [Fact]
    public async Task CreateTableAsync_PostgreSql_reaches_the_connection_stage_for_a_bare_table_name()
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(PostgreSqlConnection(), "Patient"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.AlreadyExisted.Should().BeFalse();
    }

    [Fact]
    public async Task CreateTableAsync_PostgreSql_reaches_the_connection_stage_for_a_public_schema_qualified_name()
    {
        // The live schema probe reports an existing PostgreSQL table's fullName as "public.Patient" (its
        // real default schema) — SplitTableName must accept this shape too, not just a bare name.
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(PostgreSqlConnection(), "public.Patient"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.AlreadyExisted.Should().BeFalse();
    }

    [Theory]
    [InlineData("Notes\"; DROP TABLE Users; --")]
    [InlineData("Notes Field")]
    [InlineData("1Notes")]
    public async Task AddColumnAsync_PostgreSql_rejects_a_malformed_column_name(string columnName)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(PostgreSqlConnection(), "public.Patient", columnName, "varchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AddColumnAsync_PostgreSql_reaches_the_connection_stage_for_a_valid_request()
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(PostgreSqlConnection(), "public.Patient", "Notes", "varchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
    }

    [Fact]
    public async Task AlterColumnAsync_PostgreSql_reaches_the_connection_stage_for_a_valid_request()
    {
        var result = await _sut.AlterColumnAsync(
            new AlterColumnRequest(PostgreSqlConnection(), "public.Patient", "Notes", "varchar(100)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
    }

    [Fact]
    public async Task DropColumnAsync_PostgreSql_reaches_the_connection_stage_for_a_valid_request()
    {
        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(PostgreSqlConnection(), "public.Patient", "Notes"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateTableAsync_PostgreSql_invalid_connection_fails_before_any_data_type_error()
    {
        var connection = new DestinationConnectionProbeRequest(DestinationType.PostgreSql);

        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(connection, "Patient"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Server and Database are required");
    }
}
