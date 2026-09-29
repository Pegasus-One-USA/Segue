using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Covers what is verifiable without a live Fabric tenant: table addressing, name resolution and the deterministic
/// table id. The commit loop itself needs a real OneLake container — its whole purpose is the conditional-write
/// race, which a mocked blob client would only fake — so it is exercised against a tenant rather than pretended at
/// here. The log format has its own tests in <see cref="DeltaTransactionLogTests"/>.
/// </summary>
public sealed class LakehouseTableLandingStrategyTests
{
    private const string LakehouseMetadata =
        """
        {"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"ClinicalLake",
         "dest_fabricItemType":"Lakehouse","dest_fabricMode":"lakehouseTable"}
        """;

    private static DestinationConfiguration Destination(string? metadata) =>
        new("Fabric Lakehouse", DestinationType.DataFabricAzure, new SecretReference("kv", "secret"), null, metadata);

    private static MappingProfile Mapping(string resourceType = "Patient", string destinationObject = "Patients") =>
        new("Fabric Mapping", resourceType, Guid.NewGuid(), Guid.NewGuid(), destinationObject, []);

    [Fact]
    public void Lakehouse_table_mode_parses_from_metadata()
    {
        var settings = FabricDestinationSettings.Parse(Destination(LakehouseMetadata));

        settings.Mode.Should().Be(FabricLandingMode.LakehouseTable);
        settings.ItemType.Should().Be("Lakehouse");
        settings.LakehouseSchema.Should().BeNull();
    }

    /// <summary>
    /// A Delta table lives under the item's managed <c>Tables/</c> area — the one prefix the Files surface
    /// refuses, because bare files there never register as a table. This mode writes the log that makes them one.
    /// </summary>
    [Fact]
    public void A_table_is_addressed_under_the_items_Tables_area()
    {
        var settings = FabricDestinationSettings.Parse(Destination(LakehouseMetadata));

        settings.LakehouseTableRootPath("Patient")
            .Should().Be("ClinicalLake.Lakehouse/Tables/Patient");
    }

    /// <summary>
    /// A schema-enabled Lakehouse nests tables one level deeper. The two layouts are not interchangeable, so the
    /// schema is only applied when explicitly configured — defaulting it to "dbo" would silently write to the
    /// wrong place on a classic Lakehouse.
    /// </summary>
    [Fact]
    public void A_schema_enabled_lakehouse_nests_the_table_under_its_schema()
    {
        var settings = FabricDestinationSettings.Parse(Destination(
            """
            {"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"ClinicalLake",
             "dest_fabricItemType":"Lakehouse","dest_fabricMode":"lakehouseTable",
             "dest_fabricLakehouseSchema":"gold"}
            """));

        settings.LakehouseTableRootPath("Patient")
            .Should().Be("ClinicalLake.Lakehouse/Tables/gold/Patient");
    }

    /// <summary>
    /// A GUID item name is emitted bare, with no ".Lakehouse" suffix — the addressing a tenant with OneLake
    /// friendly names disabled requires. This is the same rule the Warehouse staging path follows, and getting it
    /// wrong is what produced FriendlyNameSupportDisabled against a live tenant.
    /// </summary>
    [Fact]
    public void A_guid_item_is_addressed_bare()
    {
        var settings = FabricDestinationSettings.Parse(Destination(
            """
            {"dest_fabricWorkspace":"21fe9b8f-2349-4667-8608-3547317ea11f",
             "dest_fabricItemName":"e7688526-2d23-4d78-97cf-6ba9ad8e556c",
             "dest_fabricItemType":"Lakehouse","dest_fabricMode":"lakehouseTable"}
            """));

        settings.LakehouseTableRootPath("Patient")
            .Should().Be("e7688526-2d23-4d78-97cf-6ba9ad8e556c/Tables/Patient");
    }

    /// <summary>
    /// A Lakehouse table name is a FOLDER name, so a schema-qualified mapping target would otherwise create a
    /// folder literally called "dbo.Patient". Taking the last segment matches what the Warehouse strategy does
    /// with the same input, so one mapping profile names the same table on either surface.
    /// </summary>
    [Theory]
    [InlineData("dbo.Patient", "Patient")]
    [InlineData("Patient", "Patient")]
    [InlineData("dbo.Patient;mode=append", "Patient")]
    [InlineData("analytics.dbo.Observation", "Observation")]
    public void The_table_name_is_the_last_segment_of_the_mapping_target(string destinationObject, string expected)
    {
        var settings = FabricDestinationSettings.Parse(Destination(LakehouseMetadata));

        LakehouseTableLandingStrategy.ResolveTableName(settings, Mapping(destinationObject: destinationObject))
            .Should().Be(expected);
    }

    /// <summary>
    /// An empty or unusable mapping target falls back to the resource type, so a table always has a name rather
    /// than the write failing on an empty path segment.
    /// </summary>
    [Fact]
    public void An_unusable_mapping_target_falls_back_to_the_resource_type()
    {
        var settings = FabricDestinationSettings.Parse(Destination(LakehouseMetadata));

        LakehouseTableLandingStrategy.ResolveTableName(settings, Mapping("Observation", destinationObject: "."))
            .Should().Be("Observation");
    }

    /// <summary>
    /// An explicit destination-level table override wins over the mapping's own target — a destination-level
    /// setting is the more deliberate statement, the same precedence the Warehouse surface applies.
    /// </summary>
    [Fact]
    public void An_explicit_table_override_wins()
    {
        var settings = FabricDestinationSettings.Parse(Destination(
            """
            {"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"ClinicalLake",
             "dest_fabricItemType":"Lakehouse","dest_fabricMode":"lakehouseTable",
             "dest_fabricWarehouseTable":"PatientGold"}
            """));

        LakehouseTableLandingStrategy.ResolveTableName(settings, Mapping(destinationObject: "dbo.Patient"))
            .Should().Be("PatientGold");
    }

    /// <summary>
    /// The table id is derived, not random: a table deleted outside FHIRBridge and then recreated keeps the
    /// identity the customer considers it to have, rather than appearing as a different table each time.
    /// </summary>
    [Fact]
    public void The_table_id_is_stable_for_a_destination_and_path()
    {
        var destination = Destination(LakehouseMetadata);

        LakehouseTableLandingStrategy.DeriveTableId(destination, "L.Lakehouse/Tables/Patient")
            .Should().Be(LakehouseTableLandingStrategy.DeriveTableId(destination, "L.Lakehouse/Tables/Patient"));
    }

    /// <summary>
    /// Different tables are different identities, so the derivation must include the path rather than only the
    /// destination — otherwise every table a destination writes would claim the same Delta table id.
    /// </summary>
    [Fact]
    public void Different_tables_get_different_ids()
    {
        var destination = Destination(LakehouseMetadata);

        LakehouseTableLandingStrategy.DeriveTableId(destination, "L.Lakehouse/Tables/Patient")
            .Should().NotBe(LakehouseTableLandingStrategy.DeriveTableId(destination, "L.Lakehouse/Tables/Observation"));
    }

    /// <summary>
    /// Data file names must not collide: two writes in the same millisecond still produce distinct files, which
    /// the guid suffix guarantees. A collision would have one commit's file silently overwritten by another's.
    /// </summary>
    [Fact]
    public void Data_file_names_are_unique_per_write()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var mapping = Mapping();

        var first = LakehouseTableLandingStrategy.BuildDataFileName(mapping, timestamp);
        var second = LakehouseTableLandingStrategy.BuildDataFileName(mapping, timestamp);

        first.Should().NotBe(second);
        first.Should().StartWith("part-").And.EndWith(".parquet");
    }
}
