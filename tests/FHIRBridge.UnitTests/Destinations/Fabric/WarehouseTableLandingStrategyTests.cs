using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Covers what is verifiable without a live Fabric tenant: settings parsing, staging addressing, and the
/// item-type/mode guard. The COPY INTO and MERGE statements themselves are deliberately NOT asserted
/// string-by-string — a test that pins the SQL text only proves the strategy still emits what it emitted when the
/// test was written, which is worth little when the open question is whether Fabric accepts it at all.
/// </summary>
public sealed class WarehouseTableLandingStrategyTests
{
    private const string WarehouseMetadata =
        """
        {"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"ClinicalWh",
         "dest_fabricItemType":"Warehouse","dest_fabricMode":"warehouseTable",
         "dest_fabricWarehouseSqlEndpoint":"Server=x.datawarehouse.fabric.microsoft.com;Database=ClinicalWh",
         "dest_fabricWarehouseStagingLakehouse":"Stage"}
        """;

    private static DestinationConfiguration Destination(string? metadata) =>
        new("Fabric Warehouse", DestinationType.DataFabricAzure, new SecretReference("kv", "secret"), null, metadata);

    private static MappingProfile Mapping(string resourceType = "Patient", string destinationObject = "Patients") =>
        new("Fabric Mapping", resourceType, Guid.NewGuid(), Guid.NewGuid(), destinationObject, []);

    [Fact]
    public void Warehouse_settings_parse_with_their_defaults()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));

        settings.Mode.Should().Be(FabricLandingMode.WarehouseTable);
        settings.ItemType.Should().Be("Warehouse");
        settings.WarehouseSchema.Should().Be("dbo");
        settings.WarehouseWriteMode.Should().Be(FabricTableWriteMode.Append);
        settings.WarehouseStagingPath.Should().Be("_staging");
        settings.WarehouseStagingLakehouse.Should().Be("Stage");
    }

    /// <summary>
    /// Neither can be defaulted — the TDS endpoint is a different service from OneLake, and a Warehouse has no
    /// Files area to stage in — so both are refused at parse time rather than failing partway through a load.
    /// </summary>
    [Theory]
    [InlineData("dest_fabricWarehouseSqlEndpoint", "*Warehouse SQL endpoint*")]
    [InlineData("dest_fabricWarehouseStagingLakehouse", "*staging lakehouse*")]
    public void Warehouse_mode_requires_its_own_two_settings(string omittedKey, string expectedMessage)
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata.Remove(omittedKey);

        var act = () => FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
    }

    [Fact]
    public void Staging_path_lands_under_the_staging_lakehouses_Files_area()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));

        settings.WarehouseStagingRootPath.Should().Be("Stage.Lakehouse/Files/_staging");
    }

    /// <summary>
    /// COPY INTO reads the abfss/dfs form, not the blob endpoint the upload used. Getting this wrong produces an
    /// authentication-shaped error rather than an obvious "wrong URL", so it is worth pinning.
    /// </summary>
    [Fact]
    public void Staging_url_uses_the_dfs_endpoint_that_COPY_INTO_reads()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));
        var path = WarehouseTableLandingStrategy.BuildStagingBlobPath(settings, Mapping(), DateTime.UtcNow);

        WarehouseTableLandingStrategy.BuildStagingUrl(settings, path)
            .Should().StartWith("abfss://Analytics@onelake.dfs.fabric.microsoft.com/Stage.Lakehouse/Files/_staging/");
    }

    /// <summary>
    /// Two concurrent runs of one route must not read each other's staging file — that failure shows up as a
    /// duplicated or truncated load, which is far harder to diagnose than a missing file.
    /// </summary>
    [Fact]
    public void Each_load_stages_to_its_own_file()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));
        var timestamp = DateTime.UtcNow;

        var first = WarehouseTableLandingStrategy.BuildStagingBlobPath(settings, Mapping(), timestamp);
        var second = WarehouseTableLandingStrategy.BuildStagingBlobPath(settings, Mapping(), timestamp);

        first.Should().NotBe(second, "a per-load guid keeps concurrent runs of one route apart");
        first.Should().EndWith(".parquet");
    }

    [Fact]
    public void Table_name_falls_back_to_the_mapping_destination_object()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));

        settings.QualifiedWarehouseTable("Patients").Should().Be("[dbo].[Patients]");
    }

    [Fact]
    public void An_explicit_table_and_schema_win_over_the_fallback()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWarehouseSchema"] = "clinical";
        metadata["dest_fabricWarehouseTable"] = "PatientRows";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        settings.QualifiedWarehouseTable("Patients").Should().Be("[clinical].[PatientRows]");
    }

    /// <summary>
    /// A Lakehouse's SQL analytics endpoint is read-only, so a Warehouse COPY INTO aimed at one cannot work. The
    /// pairing is refused before the write rather than inside a protocol call that names neither half.
    /// </summary>
    [Fact]
    public void Warehouse_strategy_refuses_a_lakehouse_item_type()
    {
        new WarehouseTableLandingStrategy(null!, null!, null!)
            .SupportedItemTypes.Should().BeEquivalentTo(["Warehouse"]);
    }

    [Fact]
    public void OneLake_files_strategy_refuses_a_warehouse_item_type()
    {
        new OneLakeFilesLandingStrategy(null!, null!)
            .SupportedItemTypes.Should().BeEquivalentTo(["Lakehouse"]);
    }

    /// <summary>
    /// FHIRBridge never owns a customer's destination schema (docs/backend/11-destination-schema-ownership-plan.md),
    /// so the target table is verified and never created — the same rule section 3.A.1-2 applied to
    /// MappedSqlServerDestinationWriter. DestinationWriterNoDdlTests enforces this across the whole Destinations
    /// tree; this asserts the one intentional exception stays what it claims to be, a per-load scratch table that
    /// is dropped again, rather than quietly growing into customer-table DDL.
    /// </summary>
    [Fact]
    public void The_only_DDL_is_the_per_load_scratch_table_never_the_customers_table()
    {
        var source = File.ReadAllText(LocateStrategySource());

        source.Should().NotContain(
            "CREATE TABLE {qualifiedTable}", "the customer's target table is never created by FHIRBridge");
        source.Should().Contain(
            "does not exist in the Fabric Warehouse", "a missing target table fails with an actionable message");
        source.Should().Contain(
            "CREATE TABLE {stagingTable}", "the MERGE source is FHIRBridge's own scratch table");
        source.Should().Contain(
            "DROP TABLE IF EXISTS {stagingTable}", "the scratch table is always dropped again");
    }

    private static string LocateStrategySource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FHIRBridge.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the FHIRBridge.sln anchor is required to locate the src tree");
        return Path.Combine(
            dir!.FullName,
            "src", "FHIRBridge.Infrastructure", "Destinations", "Fabric", "WarehouseTableLandingStrategy.cs");
    }
}
