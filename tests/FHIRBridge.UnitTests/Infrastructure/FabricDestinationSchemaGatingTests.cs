using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Pins the CURRENT, shipped behaviour of every schema entry point for <see cref="DestinationType.DataFabricAzure"/>
/// before Fabric Warehouse schema support widens it.
///
/// <para><b>Why this file exists.</b> Fabric is one DestinationType with four landing modes
/// (<c>dest_fabricMode</c>), and only <c>WarehouseTable</c> is relational. The OneLake Files mode is in
/// production use and tested; the Warehouse work must not change it. <c>IsRelational</c> currently keys off
/// DestinationType alone, so it answers <c>false</c> for the whole Fabric type — which is exactly right for
/// OneLake Files and exactly wrong for Warehouse. Making it mode-aware is the change these tests guard: every
/// assertion below must still hold afterwards, because every request here carries no Warehouse mode.</para>
///
/// <para>These are deliberately BEHAVIOUR pins through the public service, not tests of the private
/// <c>IsRelational</c> predicate — the gate is only meaningful in terms of what a caller gets back. A probe
/// that returns <c>Connected=false</c> with a "not a relational database" error is what makes the mapping UI
/// render its file card instead of a table picker (see field-mapping-model.ts's SQL_FAMILY_TYPES, which
/// mirrors this rule client-side).</para>
/// </summary>
public sealed class FabricDestinationSchemaGatingTests
{
    private readonly SqlDestinationSchemaService _sut = new(
        Mock.Of<IConfigurationRepository>(),
        Mock.Of<ISecretProvider>(),
        Mock.Of<ISqlConnectionSecretMerger>());

    /// <summary>
    /// A Fabric connection as the schema entry points see one today. Note there is no landing-mode field on
    /// <see cref="DestinationConnectionProbeRequest"/> at all — which is itself part of what the Warehouse work
    /// has to address, and why every case here is unambiguously "not Warehouse".
    /// </summary>
    private static DestinationConnectionProbeRequest FabricConnection() => new(
        DestinationType.DataFabricAzure, Server: "onelake.dfs.fabric.microsoft.com", Database: "Lakehouse");

    [Fact]
    public async Task ProbeSchemaAsync_Fabric_is_not_relational_so_no_tables_are_listed()
    {
        var result = await _sut.ProbeSchemaAsync(FabricConnection(), CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Tables.Should().BeEmpty();
        // The message the mapping UI surfaces when it falls back to the file card.
        result.Error.Should().Contain("not a relational database");
    }

    [Fact]
    public async Task CreateTableAsync_Fabric_is_refused()
    {
        var result = await _sut.CreateTableAsync(
            new CreateTableRequest(FabricConnection(), "dbo.Patient"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support table creation");
    }

    [Fact]
    public async Task AddColumnAsync_Fabric_is_refused()
    {
        var result = await _sut.AddColumnAsync(
            new AddColumnRequest(FabricConnection(), "dbo.Patient", "Notes", "nvarchar(50)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support column creation");
    }

    [Fact]
    public async Task DropColumnAsync_Fabric_is_refused()
    {
        var result = await _sut.DropColumnAsync(
            new DropColumnRequest(FabricConnection(), "dbo.Patient", "Notes"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support dropping columns");
    }

    [Fact]
    public async Task AlterColumnAsync_Fabric_is_refused()
    {
        var result = await _sut.AlterColumnAsync(
            new AlterColumnRequest(FabricConnection(), "dbo.Patient", "Notes", "nvarchar(100)"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support altering columns");
    }

    /// <summary>
    /// The non-relational destinations that must keep answering exactly this way — OneLake Files' neighbours in
    /// the same "no live schema" family (see field-mapping-model.ts's MappingDestType union). Guards against a
    /// widening of IsRelational that reaches further than Fabric Warehouse.
    /// </summary>
    /// <summary>
    /// Regression: the Fabric Warehouse schema read must not run the SQL Server CONSTRAINT query. Fabric does
    /// not enforce PRIMARY KEY/UNIQUE and does not expose INFORMATION_SCHEMA.KEY_COLUMN_USAGE /
    /// TABLE_CONSTRAINTS, so that join throws — which the Test Connection probe then swallowed, reporting a
    /// connected warehouse with an empty table list and no error at all.
    ///
    /// <para>No live Warehouse is reachable here, so this asserts the reachable half: the request is refused at
    /// the CONNECTION stage (no Fabric factory is supplied to this instance) rather than at a query stage,
    /// proving nothing before the connection depends on constraint metadata. The query itself is exercised
    /// against a real tenant by the manual smoke test.</para>
    /// </summary>
    [Fact]
    public async Task ProbeSchemaAsync_FabricWarehouse_is_relational_and_fails_only_at_the_connection()
    {
        var result = await _sut.ProbeSchemaAsync(
            new DestinationConnectionProbeRequest(
                DestinationType.DataFabricWarehouse,
                FabricWorkspace: "ws",
                FabricItemName: "wh",
                FabricWarehouseSqlEndpoint: "x.datawarehouse.fabric.microsoft.com"),
            CancellationToken.None);

        result.Connected.Should().BeFalse();
        // NOT "is not a relational database" — that message would mean IsRelational still excluded it.
        result.Error.Should().NotContain("not a relational database");
        result.Error.Should().Contain("Fabric Warehouse connection factory");
    }

    [Theory]
    [InlineData(DestinationType.DataFabricAzure)]
    [InlineData(DestinationType.DataLakeWebhook)]
    [InlineData(DestinationType.Mongo)]
    [InlineData(DestinationType.Csv)]
    public async Task ProbeSchemaAsync_non_relational_destinations_list_no_tables(DestinationType destinationType)
    {
        var result = await _sut.ProbeSchemaAsync(
            new DestinationConnectionProbeRequest(destinationType, Server: "localhost", Database: "x"),
            CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Tables.Should().BeEmpty();
    }
}
