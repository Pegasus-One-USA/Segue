using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Blob;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Mocks <see cref="BlobContainerClient"/>/<see cref="BlobClient"/> (both ship with a protected parameterless
/// constructor and virtual members for exactly this) so the OneLake path layout and payload can be asserted with no
/// Fabric tenant involved.
/// </summary>
public sealed class MappedDataFabricDestinationWriterTests
{
    private readonly Mock<IOneLakeClientFactory> _clientFactory = new();
    private readonly Mock<BlobContainerClient> _workspace = new();
    private readonly Mock<BlobClient> _blob = new();

    private string? _capturedPath;
    private MemoryStream? _uploadedContent;
    private BlobUploadOptions? _uploadOptions;

    private const string BaseMetadata =
        """{"dest_fabricWorkspace":"Analytics","dest_fabricItemName":"ClinicalLake"}""";

    public MappedDataFabricDestinationWriterTests()
    {
        _clientFactory
            .Setup(f => f.GetWorkspaceAsync(
                It.IsAny<DestinationConfiguration>(),
                It.IsAny<FabricDestinationSettings>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobDestinationTarget(_workspace.Object, SupportsContainerCreate: false));

        _workspace
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(path => _capturedPath = path)
            .Returns(_blob.Object);

        _blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, BlobUploadOptions, CancellationToken>((stream, options, _) =>
            {
                _uploadedContent = new MemoryStream();
                stream.CopyTo(_uploadedContent);
                _uploadedContent.Position = 0;
                _uploadOptions = options;
            })
            .ReturnsAsync(UploadResponse());
    }

    private static Response<BlobContentInfo> UploadResponse() =>
        Response.FromValue(
            BlobsModelFactory.BlobContentInfo(
                eTag: new ETag("\"x\""),
                lastModified: DateTimeOffset.UtcNow,
                contentHash: null,
                encryptionKeySha256: null,
                blobSequenceNumber: 0),
            Mock.Of<Response>());

    private static DestinationConfiguration Destination(string? connectionMetadataJson = BaseMetadata) =>
        new("Fabric Lake", DestinationType.DataFabricAzure, new SecretReference("kv", "secret"), null, connectionMetadataJson);

    private static MappingProfile Mapping(string resourceType = "Patient", string destinationObject = "Patient") =>
        new("Fabric Mapping", resourceType, Guid.NewGuid(), Guid.NewGuid(), destinationObject, []);

    private static MappedDestinationRecord Record(string id = "p1") =>
        new(Guid.NewGuid(), "Patient", "Patient", id, new Dictionary<string, object?> { ["Name"] = "Alice" });

    private static PipelineWriteContext Context() => new(false, "Fabric Export Workflow", DateTimeOffset.UtcNow);

    /// <summary>
    /// Built through the real registry and real OneLakeFilesLandingStrategy rather than a stub, so these tests
    /// keep covering the whole dispatch path — writer → registry → strategy → OneLake client — end to end.
    /// </summary>
    private MappedDataFabricDestinationWriter CreateWriter()
    {
        var strategy = new OneLakeFilesLandingStrategy(
            _clientFactory.Object, NullLogger<OneLakeFilesLandingStrategy>.Instance);
        return new MappedDataFabricDestinationWriter(new FabricLandingStrategyRegistry([strategy]));
    }

    private string UploadedText() => Encoding.UTF8.GetString(_uploadedContent!.ToArray());

    [Fact]
    public async Task An_empty_batch_never_touches_OneLake()
    {
        var result = await CreateWriter().WriteAsync(
            Destination(), Mapping(), [], Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        _clientFactory.Verify(
            f => f.GetWorkspaceAsync(It.IsAny<DestinationConfiguration>(), It.IsAny<FabricDestinationSettings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task The_path_addresses_the_item_then_Files_then_the_hive_style_resource_partition()
    {
        var result = await CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record("p1"), Record("p2")], Context(), CancellationToken.None);

        result.Count.Should().Be(2);
        _capturedPath.Should().NotBeNull();
        Regex.IsMatch(
                _capturedPath!,
                @"^ClinicalLake\.Lakehouse/Files/fhirbridge/resourceType=Patient/Patient_\d{17}\.ndjson$")
            .Should().BeTrue(_capturedPath);
    }

    [Fact]
    public async Task Date_partitioning_adds_a_hive_style_ingest_date_folder()
    {
        await CreateWriter().WriteAsync(
            Destination(
                """{"dest_fabricWorkspace":"A","dest_fabricItemName":"L","dest_fabricPartitionBy":"resourceTypeAndIngestDate"}"""),
            Mapping(),
            [Record()],
            Context(),
            CancellationToken.None);

        _capturedPath.Should().Contain("/resourceType=Patient/ingest_date=")
            .And.Contain(DateTime.UtcNow.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task Partitioning_can_be_turned_off_entirely()
    {
        await CreateWriter().WriteAsync(
            Destination("""{"dest_fabricWorkspace":"A","dest_fabricItemName":"L","dest_fabricPartitionBy":"none"}"""),
            Mapping(),
            [Record()],
            Context(),
            CancellationToken.None);

        _capturedPath.Should().NotContain("resourceType=").And.NotContain("ingest_date=");
    }

    [Fact]
    public async Task Ndjson_output_is_LF_delimited_with_one_line_per_record()
    {
        await CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record("p1"), Record("p2"), Record("p3")], Context(), CancellationToken.None);

        var text = UploadedText();
        text.Split('\n').Should().HaveCount(3);
        text.Should().NotContain("\r", "CRLF is exactly what breaks a Spark/Fabric NDJSON reader");
        _uploadOptions!.HttpHeaders!.ContentType.Should().Be("application/x-ndjson");
    }

    [Fact]
    public async Task Csv_output_writes_a_csv_file_with_the_csv_content_type()
    {
        await CreateWriter().WriteAsync(
            Destination("""{"dest_fabricWorkspace":"A","dest_fabricItemName":"L","dest_fabricFileFormat":"csv"}"""),
            Mapping(),
            [Record()],
            Context(),
            CancellationToken.None);

        _capturedPath.Should().EndWith(".csv");
        _uploadOptions!.HttpHeaders!.ContentType.Should().Be("text/csv");
        UploadedText().Should().Contain("Name");
    }

    [Fact]
    public async Task Parquet_output_writes_a_real_parquet_file()
    {
        await CreateWriter().WriteAsync(
            Destination("""{"dest_fabricWorkspace":"A","dest_fabricItemName":"L","dest_fabricFileFormat":"parquet"}"""),
            Mapping(),
            [Record()],
            Context(),
            CancellationToken.None);

        _capturedPath.Should().EndWith(".parquet");
        _uploadOptions!.HttpHeaders!.ContentType.Should().Be("application/vnd.apache.parquet");

        // "PAR1" is the Parquet magic number at both ends of a valid file.
        var bytes = _uploadedContent!.ToArray();
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("PAR1");
        Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4).Should().Be("PAR1");
    }

    [Fact]
    public async Task Blob_metadata_carries_run_provenance_with_an_ascii_safe_route_name()
    {
        await CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        _uploadOptions!.Metadata.Should().Contain(new KeyValuePair<string, string>("resourceType", "Patient"));
        _uploadOptions.Metadata.Should().Contain(new KeyValuePair<string, string>("recordCount", "1"));
        _uploadOptions.Metadata["routeName"].Should().Be("Fabric Export Workflow");
    }

    [Fact]
    public async Task No_access_tier_is_sent_because_OneLake_has_no_storage_tier_concept()
    {
        await CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        _uploadOptions!.AccessTier.Should().BeNull();
    }

    // ---------------------------------------------------------------- file naming

    [Fact]
    public void A_destination_object_carrying_a_write_mode_suffix_never_reaches_the_file_name()
    {
        var settings = FabricDestinationSettings.Parse(Destination());

        var path = OneLakeFilesLandingStrategy.BuildFilePath(
            settings, Mapping(destinationObject: "Patient;mode=upsert"), new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc));

        path.Should().EndWith("Patient_20260908120000000.ndjson");
        path.Should().NotContain("mode=upsert");
    }

    [Fact]
    public void A_destination_object_saved_with_a_csv_extension_does_not_produce_a_double_extension()
    {
        var settings = FabricDestinationSettings.Parse(Destination());

        var path = OneLakeFilesLandingStrategy.BuildFilePath(
            settings, Mapping(destinationObject: "patients.csv"), new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc));

        path.Should().EndWith("patients_20260908120000000.ndjson");
    }

    [Fact]
    public void A_destination_object_that_sanitizes_to_nothing_falls_back_to_the_resource_type()
    {
        var settings = FabricDestinationSettings.Parse(Destination());

        var path = OneLakeFilesLandingStrategy.BuildFilePath(
            settings, Mapping(destinationObject: "?"), new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc));

        path.Should().EndWith("Patient_20260908120000000.ndjson");
    }
}
