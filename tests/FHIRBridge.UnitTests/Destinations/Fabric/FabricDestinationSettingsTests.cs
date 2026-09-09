using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

public sealed class FabricDestinationSettingsTests
{
    private const string MinimalMetadata =
        """{"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"ClinicalLake"}""";

    private static DestinationConfiguration Destination(string? target, string? connectionMetadataJson) =>
        new("Fabric Lake", DestinationType.DataFabricAzure, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    [Fact]
    public void Defaults_land_ndjson_in_the_items_Files_area_partitioned_by_resource_type()
    {
        var settings = FabricDestinationSettings.Parse(Destination(null, MinimalMetadata));

        settings.Mode.Should().Be(FabricLandingMode.OneLakeFiles);
        settings.AuthMode.Should().Be(FabricAuthMode.ManagedIdentity);
        settings.ItemType.Should().Be("Lakehouse");
        settings.FileFormat.Should().Be(FabricFileFormat.Ndjson);
        settings.Partitioning.Should().Be(FabricPartitionScheme.ResourceType);
        settings.BasePath.Should().Be("fhirbridge");
        settings.RootPath.Should().Be("ClinicalLake.Lakehouse/Files/fhirbridge");
        settings.AccountUrl.Should().Be("https://onelake.blob.fabric.microsoft.com");
        settings.RequiresSecret.Should().BeFalse("managed identity resolves no Key Vault secret");
    }

    [Fact]
    public void Workspace_falls_back_to_Target_for_a_row_created_through_the_existing_connection_path()
    {
        var settings = FabricDestinationSettings.Parse(
            Destination("WorkspaceFromTarget", """{"dest_fabricItemName":"ClinicalLake"}"""));

        settings.Workspace.Should().Be("WorkspaceFromTarget");
    }

    [Fact]
    public void Workspace_metadata_wins_over_Target()
    {
        var settings = FabricDestinationSettings.Parse(Destination("FromTarget", MinimalMetadata));

        settings.Workspace.Should().Be("Analytics");
    }

    [Fact]
    public void A_missing_workspace_is_reported_before_any_write_is_attempted()
    {
        var act = () => FabricDestinationSettings.Parse(
            Destination(null, """{"dest_fabricItemName":"ClinicalLake"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*workspace*");
    }

    [Fact]
    public void A_missing_item_name_is_reported_before_any_write_is_attempted()
    {
        var act = () => FabricDestinationSettings.Parse(
            Destination(null, """{"dest_fabricWorkspace":"Analytics"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*item name*");
    }

    [Theory]
    [InlineData("Analytics/Nested")]
    [InlineData("Analytics\\Nested")]
    public void A_workspace_containing_a_path_separator_is_refused_because_it_would_retarget_the_write(string workspace)
    {
        var act = () => FabricDestinationSettings.Parse(
            Destination(workspace, """{"dest_fabricItemName":"ClinicalLake"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*path separator*");
    }

    [Fact]
    public void Service_principal_mode_requires_a_tenant_and_client_id_and_does_need_a_secret()
    {
        var act = () => FabricDestinationSettings.Parse(Destination(
            null,
            """{"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"L","dest_fabricAuthMode":"servicePrincipal"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*tenant id*");

        var complete = FabricDestinationSettings.Parse(Destination(
            null,
            """{"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"L","dest_fabricAuthMode":"servicePrincipal","dest_fabricTenantId":"t","dest_fabricClientId":"c"}"""));

        complete.RequiresSecret.Should().BeTrue();
    }

    [Fact]
    public void Eventstream_mode_points_the_caller_at_the_data_lake_webhook_destination_instead()
    {
        var act = () => FabricDestinationSettings.Parse(Destination(
            null,
            """{"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"L","dest_fabricMode":"eventstream"}"""));

        act.Should().Throw<NotSupportedException>().WithMessage("*Data Lake Webhook destination*");
    }

    [Fact]
    public void Warehouse_mode_fails_fast_rather_than_half_writing()
    {
        var act = () => FabricDestinationSettings.Parse(Destination(
            null,
            """{"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"L","dest_fabricMode":"warehouseTable"}"""));

        act.Should().Throw<NotSupportedException>().WithMessage("*not implemented yet*");
    }

    [Theory]
    [InlineData("ndjson", FabricFileFormat.Ndjson, "ndjson", "application/x-ndjson")]
    [InlineData("parquet", FabricFileFormat.Parquet, "parquet", "application/vnd.apache.parquet")]
    [InlineData("csv", FabricFileFormat.Csv, "csv", "text/csv")]
    public void File_format_drives_the_extension_and_content_type(
        string configured, FabricFileFormat expected, string extension, string contentType)
    {
        var settings = FabricDestinationSettings.Parse(Destination(
            null,
            $$"""{"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"L","dest_fabricFileFormat":"{{configured}}"}"""));

        settings.FileFormat.Should().Be(expected);
        settings.FileExtension.Should().Be(extension);
        settings.ContentType.Should().Be(contentType);
    }

    [Fact]
    public void An_unsupported_item_type_is_rejected_with_the_supported_list()
    {
        var act = () => FabricDestinationSettings.Parse(Destination(
            null,
            """{"dest_fabricWorkspace":"A","dest_fabricItemName":"L","dest_fabricItemType":"Notebook"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported Fabric item type*");
    }

    // ---------------------------------------------------------------- path normalization

    [Theory]
    [InlineData(null, "fhirbridge")]
    [InlineData("", "fhirbridge")]
    [InlineData("clinical", "clinical")]
    [InlineData("/clinical/raw/", "clinical/raw")]
    [InlineData("clinical\\raw", "clinical/raw")]
    [InlineData("Files/clinical", "clinical")]
    public void Base_paths_normalize_to_a_clean_relative_path_under_Files(string? configured, string expected)
    {
        FabricDestinationSettings.NormalizeBasePath(configured, "Fabric Lake").Should().Be(expected);
    }

    [Theory]
    [InlineData("Tables")]
    [InlineData("Tables/Patient")]
    [InlineData("Files/Tables/Patient")]
    public void A_Tables_path_is_refused_because_a_Fabric_table_is_a_Delta_table(string configured)
    {
        var act = () => FabricDestinationSettings.NormalizeBasePath(configured, "Fabric Lake");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Delta*")
            .WithMessage("*shortcut, notebook or pipeline*");
    }

    [Fact]
    public void A_full_url_is_refused_rather_than_producing_a_nonsense_blob_path()
    {
        var act = () => FabricDestinationSettings.NormalizeBasePath(
            "https://onelake.dfs.fabric.microsoft.com/ws/item.Lakehouse/Files/x", "Fabric Lake");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a full URL*");
    }

    [Fact]
    public void A_path_repeating_the_item_name_is_refused_because_it_would_nest_a_second_item_folder()
    {
        var act = () => FabricDestinationSettings.NormalizeBasePath("ClinicalLake.Lakehouse/Files/raw", "Fabric Lake");

        act.Should().Throw<InvalidOperationException>().WithMessage("*must not repeat the item name*");
    }

    [Theory]
    [InlineData("clinical/../../escape")]
    [InlineData("./clinical")]
    public void Relative_traversal_segments_are_refused(string configured)
    {
        var act = () => FabricDestinationSettings.NormalizeBasePath(configured, "Fabric Lake");

        act.Should().Throw<InvalidOperationException>().WithMessage("*'.' or '..'*");
    }

    [Fact]
    public void A_blank_base_path_still_produces_a_valid_root_under_Files()
    {
        var settings = FabricDestinationSettings.Parse(Destination(
            null, """{"dest_fabricWorkspace":"A","dest_fabricItemName":"L","dest_fabricPath":"   "}"""));

        settings.RootPath.Should().Be("L.Lakehouse/Files/fhirbridge");
    }
}
