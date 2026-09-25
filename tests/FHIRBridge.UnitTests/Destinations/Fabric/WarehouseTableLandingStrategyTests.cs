using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
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
    /// The documented default for a OneLake source is NO credential clause: "The executing user's Microsoft Entra
    /// identity is the default credential for source access." Emitting one unconditionally would demand a
    /// provisioned workspace identity that most tenants do not have.
    /// </summary>
    [Fact]
    public void Copy_into_emits_no_credential_clause_by_default()
    {
        var sql = WarehouseTableLandingStrategy.BuildCopyInto(
            "[dbo].[Patient]", ["Id", "FamilyName"], "https://onelake.dfs.fabric.microsoft.com/ws/L.Lakehouse/Files/x.parquet",
            useWorkspaceIdentity: false);

        sql.Should().Contain("FILE_TYPE = 'PARQUET'");
        sql.Should().NotContain("CREDENTIAL");
    }

    /// <summary>
    /// Opt-in path for a tenant where the connecting identity cannot read the staging Lakehouse itself: COPY INTO
    /// impersonates the workspace identity for the SOURCE READ only. No SECRET is emitted — the two credential
    /// forms that take one (SAS, Storage Account Key) do not apply to a OneLake source.
    /// </summary>
    [Fact]
    public void Copy_into_can_impersonate_the_workspace_identity()
    {
        var sql = WarehouseTableLandingStrategy.BuildCopyInto(
            "[dbo].[Patient]", ["Id"], "https://onelake.dfs.fabric.microsoft.com/ws/L.Lakehouse/Files/x.parquet",
            useWorkspaceIdentity: true);

        sql.Should().Contain("CREDENTIAL = (IDENTITY = 'Workspace Identity')");
        sql.Should().NotContain("SECRET");
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

    /// <summary>
    /// Fabric's own UI shows a bare SERVER NAME, so that is what a user pastes. Handing it straight to
    /// SqlConnection fails with "Format of the initialization string does not conform to specification starting
    /// at index 0", which names neither the field nor the expected shape — so a value with no '=' is treated as
    /// a host and wrapped into a real connection string instead of rejected.
    /// </summary>
    [Fact]
    public void A_bare_server_name_is_wrapped_into_a_connection_string()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWarehouseSqlEndpoint"] = "abc123.datawarehouse.fabric.microsoft.com";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        settings.WarehouseConnectionString.Should()
            .StartWith("Server=abc123.datawarehouse.fabric.microsoft.com;")
            .And.Contain("Database=ClinicalWh", "the warehouse item name is the database");
    }

    [Fact]
    public void A_full_connection_string_is_passed_through_untouched()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWarehouseSqlEndpoint"] = "Server=x.datawarehouse.fabric.microsoft.com;Database=Other";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        settings.WarehouseConnectionString.Should()
            .Be("Server=x.datawarehouse.fabric.microsoft.com;Database=Other");
    }

    /// <summary>A connection string that names a server but no database still needs one to connect.</summary>
    [Fact]
    public void A_connection_string_without_a_database_gets_the_warehouse_item_name()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWarehouseSqlEndpoint"] = "Server=x.datawarehouse.fabric.microsoft.com";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        settings.WarehouseConnectionString.Should().EndWith(";Database=ClinicalWh");
    }

    /// <summary>
    /// Some tenants disable OneLake friendly names, so workspaces and items must be addressed by GUID. An item
    /// GUID takes NO ".Lakehouse" suffix — OneLake addresses an item either by name-plus-type or by id, never by
    /// id with a type appended. Getting this wrong is invisible until COPY INTO runs: the blob endpoint accepts a
    /// friendly name so the staging upload succeeds, while the DFS endpoint COPY INTO reads through rejects it as
    /// an "unsupported URL" (FriendlyNameSupportDisabled on a direct call).
    /// </summary>
    [Fact]
    public void A_staging_lakehouse_given_as_a_guid_is_addressed_without_the_item_type_suffix()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWarehouseStagingLakehouse"] = "8f14e45f-ceea-467a-9f3a-3d3d3d3d3d3d";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        settings.WarehouseStagingRootPath.Should()
            .Be("8f14e45f-ceea-467a-9f3a-3d3d3d3d3d3d/Files/_staging")
            .And.NotContain(".Lakehouse", "an item id is not suffixed with its type");
    }

    [Fact]
    public void A_staging_lakehouse_given_as_a_name_keeps_its_item_type_suffix()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));

        settings.WarehouseStagingRootPath.Should().Be("Stage.Lakehouse/Files/_staging");
    }

    /// <summary>The workspace half of the same URL — it is passed through verbatim, name or GUID.</summary>
    [Fact]
    public void A_workspace_guid_flows_into_the_staging_url_unchanged()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWorkspace"] = "21fe9b8f-2349-4667-8608-3547317ea11f";
        metadata["dest_fabricWarehouseStagingLakehouse"] = "8f14e45f-ceea-467a-9f3a-3d3d3d3d3d3d";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));
        var path = WarehouseTableLandingStrategy.BuildStagingBlobPath(settings, Mapping(), DateTime.UtcNow);

        WarehouseTableLandingStrategy.BuildStagingUrl(settings, path).Should().StartWith(
            "https://onelake.dfs.fabric.microsoft.com/21fe9b8f-2349-4667-8608-3547317ea11f/"
                + "8f14e45f-ceea-467a-9f3a-3d3d3d3d3d3d/Files/_staging/");
    }

    /// <summary>
    /// Regression: COPY INTO named PipelineRunId/ResourceType/DestinationObject/SourceResourceId/WrittenOnUtc
    /// alongside the mapped fields, because the shared GetColumns helper prepends those lineage columns. A
    /// customer-owned table has none of them — the relational writers stopped injecting system columns per
    /// docs/backend/11-destination-schema-ownership-plan.md — so Fabric rejected the load with "invalid metadata
    /// for column 'PipelineRunId'". The Warehouse load must use the mapped columns only.
    /// </summary>
    [Fact]
    public void Only_mapped_columns_are_loaded_never_the_lineage_columns()
    {
        var record = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patients", "p1",
            new Dictionary<string, object?> { ["Id"] = "1", ["Family"] = "Smith" });

        var mapped = MappedDestinationSerialization.GetMappedColumns([record]);

        mapped.Should().BeEquivalentTo(["Family", "Id"]);
        mapped.Should().NotContain("PipelineRunId", "a customer-owned table has no lineage columns");
        mapped.Should().NotContain("ResourceType").And.NotContain("WrittenOnUtc");

        // The unfiltered helper still carries them, for the writers that legitimately want an audit trail.
        MappedDestinationSerialization.GetColumns([record]).Should().Contain("PipelineRunId");
    }

    [Fact]
    public void Staging_path_lands_under_the_staging_lakehouses_Files_area()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));

        settings.WarehouseStagingRootPath.Should().Be("Stage.Lakehouse/Files/_staging");
    }

    /// <summary>
    /// COPY INTO reads the https/dfs form documented for a OneLake source
    /// (https://onelake.dfs.&lt;suffix&gt;/&lt;workspace&gt;/&lt;item&gt;/Files/...), not the abfss form and not the
    /// blob endpoint the upload used.
    ///
    /// <para>Regression: the abfss form was rejected by a live tenant with "Access token couldn't be fetched for
    /// storage path ... as it's an unsupported URL or cause of a transient error" — an authentication-shaped
    /// message for what is really a URL-shape problem, which is exactly why this is worth pinning.</para>
    /// </summary>
    [Fact]
    public void Staging_url_uses_the_https_dfs_form_that_COPY_INTO_reads()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));
        var path = WarehouseTableLandingStrategy.BuildStagingBlobPath(settings, Mapping(), DateTime.UtcNow);

        var url = WarehouseTableLandingStrategy.BuildStagingUrl(settings, path);

        url.Should().StartWith(
            "https://onelake.dfs.fabric.microsoft.com/Analytics/Stage.Lakehouse/Files/_staging/");
        url.Should().NotStartWith("abfss://");
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

    /// <summary>
    /// Regression: a mapping profile names its target SCHEMA-QUALIFIED ("dbo.Patient_NewMapped"), exactly as
    /// the mapping canvas writes it. The schema part must be recognised as a schema, not folded into the table
    /// name — doing the latter produced "[dbo].[dbo_Patient_NewMapped]", a table no warehouse has, and the run
    /// failed with "Destination table ... does not exist" against a table the user had just created.
    /// </summary>
    [Fact]
    public void A_schema_qualified_mapping_target_is_split_not_flattened()
    {
        var settings = FabricDestinationSettings.Parse(Destination(WarehouseMetadata));

        settings.QualifiedWarehouseTable("Patient_NewMapped", "dbo")
            .Should().Be("[dbo].[Patient_NewMapped]");
    }

    /// <summary>The mapping's own schema is more specific than the destination-level default, so it wins.</summary>
    [Fact]
    public void A_mapping_schema_overrides_the_destination_default_schema()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWarehouseSchema"] = "staging";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        settings.QualifiedWarehouseTable("Patients", "clinical").Should().Be("[clinical].[Patients]");
    }

    /// <summary>A bare table name carries no schema, so the destination's own schema still applies.</summary>
    [Fact]
    public void A_bare_mapping_target_keeps_the_destination_schema()
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(WarehouseMetadata)!.AsObject();
        metadata["dest_fabricWarehouseSchema"] = "staging";

        var settings = FabricDestinationSettings.Parse(Destination(metadata.ToJsonString()));

        settings.QualifiedWarehouseTable("Patients", null).Should().Be("[staging].[Patients]");
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
