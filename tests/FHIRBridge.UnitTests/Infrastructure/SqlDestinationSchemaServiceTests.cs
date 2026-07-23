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
/// (ALTER TABLE ADD COLUMN / CREATE TABLE): the identifier allowlist and the data-type allowlist.
/// Both are exercised through the public service so a rejection is proven to short-circuit before
/// any connection is attempted — no live database needed, since a malformed identifier/type is
/// rejected before <c>BuildConnectionString</c>/<c>OpenConnectionAsync</c> ever run.
/// </summary>
public sealed class SqlDestinationSchemaServiceTests
{
    private readonly SqlDestinationSchemaService _sut = new(
        Mock.Of<IConfigurationRepository>(),
        Mock.Of<ISecretProvider>());

    private static DestinationConnectionProbeRequest ValidConnection() => new(
        DestinationType.SqlServer, Server: "localhost", Database: "FHIRBridge");

    [Theory]
    [InlineData("dbo.PatientContact; DROP TABLE Users")]
    [InlineData("dbo.Patient-Contact")]
    [InlineData("1dbo.Patient")]
    [InlineData("a.b.c")]
    public async Task AddColumnAsync_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(ValidConnection(), tableName, "Notes", "nvarchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Notes]; DROP TABLE Users; --")]
    [InlineData("Notes Field")]
    [InlineData("1Notes")]
    public async Task AddColumnAsync_rejects_a_malformed_column_name(string columnName)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(ValidConnection(), "dbo.PatientContact", columnName, "nvarchar(50)"),
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
    public async Task AddColumnAsync_rejects_a_disallowed_data_type(string dataType)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(ValidConnection(), "dbo.PatientContact", "Notes", dataType),
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
    public async Task AddColumnAsync_accepts_an_allowed_data_type_and_fails_only_at_the_connection_stage(
        string dataType)
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(ValidConnection(), "dbo.PatientContact", "Notes", dataType),
            CancellationToken.None);

        // A real connection will fail against "localhost" in a unit-test sandbox, but that failure must
        // come from the connection attempt, never from data-type validation rejecting a valid type.
        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("is not an allowed data type");
        result.Error.Should().NotContain("must be between 1 and 4000");
        result.Error.Should().NotContain("precision/scale");
    }

    [Fact]
    public async Task AddColumnAsync_rejects_non_sql_server_destination_types_up_front()
    {
        var connection = ValidConnection() with { DestinationType = DestinationType.PostgreSql };

        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(connection, "dbo.PatientContact", "Notes", "nvarchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support column creation");
    }

    [Fact]
    public async Task CreateTableAsync_rejects_non_sql_server_destination_types_up_front()
    {
        var connection = ValidConnection() with { DestinationType = DestinationType.MySql };

        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(connection, "dbo.NewTable"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support table creation");
    }

    [Theory]
    [InlineData("dbo.NewTable; DROP TABLE Users")]
    [InlineData("a.b.c")]
    [InlineData("1NewTable")]
    public async Task CreateTableAsync_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(ValidConnection(), tableName),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DropColumnAsync_rejects_non_sql_server_destination_types_up_front()
    {
        var connection = ValidConnection() with { DestinationType = DestinationType.PostgreSql };

        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(connection, "dbo.PatientContact", "Notes"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support dropping columns");
    }

    [Theory]
    [InlineData("dbo.PatientContact; DROP TABLE Users")]
    [InlineData("dbo.Patient-Contact")]
    [InlineData("1dbo.Patient")]
    [InlineData("a.b.c")]
    public async Task DropColumnAsync_rejects_a_malformed_table_name(string tableName)
    {
        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(ValidConnection(), tableName, "Notes"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Notes]; DROP TABLE Users; --")]
    [InlineData("Notes Field")]
    [InlineData("1Notes")]
    public async Task DropColumnAsync_rejects_a_malformed_column_name(string columnName)
    {
        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(ValidConnection(), "dbo.PatientContact", columnName),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
