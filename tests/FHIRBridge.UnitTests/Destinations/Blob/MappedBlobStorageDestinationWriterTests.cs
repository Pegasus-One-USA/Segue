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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.Blob;

/// <summary>
/// <see cref="BlobContainerClient"/>/<see cref="BlobClient"/> ship with a protected parameterless constructor and
/// virtual members specifically so consumers can mock them (Moq/Castle DynamicProxy) without a real storage
/// account — the pattern Microsoft's own Azure SDK testing docs recommend.
/// </summary>
public sealed class MappedBlobStorageDestinationWriterTests
{
    private readonly Mock<IBlobContainerClientFactory> _clientFactory = new();
    private readonly Mock<BlobContainerClient> _container = new();

    private static DestinationConfiguration Destination(string? connectionMetadataJson = null) =>
        new("Blob Export", DestinationType.BlobStorage, new SecretReference("kv", "secret"), "fhir", connectionMetadataJson);

    private static MappingProfile Mapping(string destinationObject = "Patient", IEnumerable<MappingField>? fields = null) =>
        new("Patient Blob", "Patient", Guid.NewGuid(), Guid.NewGuid(), destinationObject, fields ?? []);

    private static MappingField UpsertKeyField(string targetField = "PatientId") =>
        new(TargetField: targetField, JsonPath: "$.id", ValueType: MappingValueType.String,
            IsRequired: false, DefaultValue: null, Format: null, IsUpsertKey: true);

    private static MappedDestinationRecord Record(string id = "p1", IReadOnlyDictionary<string, object?>? values = null) =>
        new(Guid.NewGuid(), "Patient", "Patient", id, values ?? new Dictionary<string, object?> { ["Name"] = "Alice" });

    private static PipelineWriteContext Context() => new(true, "Patient Export Workflow", DateTimeOffset.UtcNow);

    private MappedBlobStorageDestinationWriter CreateWriter() =>
        new(_clientFactory.Object, NullLogger<MappedBlobStorageDestinationWriter>.Instance);

    private void SetupTarget(bool supportsContainerCreate = true) =>
        _clientFactory
            .Setup(f => f.GetTargetAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<BlobDestinationSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobDestinationTarget(_container.Object, supportsContainerCreate));

    private static Response<BlobContainerInfo> CreateContainerResponse() =>
        Response.FromValue(
            BlobsModelFactory.BlobContainerInfo(new ETag("\"x\""), DateTimeOffset.UtcNow),
            Mock.Of<Response>());

    private static Response<BlobContentInfo> UploadResponse() =>
        Response.FromValue(
            BlobsModelFactory.BlobContentInfo(
                eTag: new ETag("\"x\""),
                lastModified: DateTimeOffset.UtcNow,
                contentHash: null,
                encryptionKeySha256: null,
                blobSequenceNumber: 0),
            Mock.Of<Response>());

    private static Response<bool> ExistsResponse(bool exists) => Response.FromValue(exists, Mock.Of<Response>());

    [Fact]
    public async Task Empty_record_set_short_circuits_without_touching_the_client_factory()
    {
        var result = await CreateWriter().WriteAsync(Destination(), Mapping(), [], Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        _clientFactory.Verify(
            f => f.GetTargetAsync(It.IsAny<DestinationConfiguration>(), It.IsAny<BlobDestinationSettings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Happy_path_uploads_one_NDJSON_blob_and_returns_the_record_count()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());

        Stream? uploadedContent = null;
        BlobUploadOptions? uploadOptions = null;
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, BlobUploadOptions, CancellationToken>((stream, options, _) =>
            {
                uploadedContent = new MemoryStream();
                stream.CopyTo(uploadedContent);
                uploadedContent.Position = 0;
                uploadOptions = options;
            })
            .ReturnsAsync(UploadResponse());

        var records = new[] { Record("p1"), Record("p2") };
        var result = await CreateWriter().WriteAsync(Destination(), Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(2);
        uploadOptions.Should().NotBeNull();
        uploadOptions!.HttpHeaders!.ContentType.Should().Be("application/x-ndjson");
        uploadOptions.Metadata!["recordCount"].Should().Be("2");
        uploadOptions.Metadata["resourceType"].Should().Be("Patient");

        uploadedContent.Should().NotBeNull();
        var content = Encoding.UTF8.GetString(((MemoryStream)uploadedContent!).ToArray());
        var lines = content.Split(Environment.NewLine);
        lines.Should().HaveCount(2);
        content.Should().Contain("p1").And.Contain("p2");
    }

    [Fact]
    public async Task Blob_name_is_stamped_from_the_mapping_profiles_destination_object_not_the_container()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        capturedName.Should().NotBeNull();
        Regex.IsMatch(capturedName!, @"^Patient_\d{17}\.ndjson$").Should().BeTrue(capturedName);
    }

    [Fact]
    public async Task PathPrefix_is_prepended_to_the_blob_name()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(
            Destination("""{"dest_blobPathPrefix":"fhirbridge/patient"}"""), Mapping(), [Record()], Context(), CancellationToken.None);

        capturedName.Should().StartWith("fhirbridge/patient/Patient_");
    }

    [Fact]
    public async Task Container_is_created_when_CreateContainerIfNotExists_is_enabled_and_supported()
    {
        SetupTarget(supportsContainerCreate: true);
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        _container.Verify(
            c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Container_create_is_skipped_when_the_flag_is_disabled()
    {
        SetupTarget(supportsContainerCreate: true);
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(
            Destination("""{"dest_blobCreateContainerIfNotExists":"false"}"""), Mapping(), [Record()], Context(), CancellationToken.None);

        _container.Verify(
            c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Container_create_is_skipped_when_the_target_does_not_support_it()
    {
        // A container-scoped SAS has no permission to create its own container — asking would 403 on every write.
        SetupTarget(supportsContainerCreate: false);
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        _container.Verify(
            c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_403_from_container_create_does_not_abort_the_upload()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "forbidden"));
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var result = await CreateWriter().WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failure_from_upload_propagates()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "forbidden"));

        var act = () => CreateWriter().WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        await act.Should().ThrowAsync<RequestFailedException>();
    }

    [Fact]
    public async Task Append_mode_strips_a_known_extension_from_the_destination_object_stem()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(
            Destination(), Mapping(destinationObject: "patients.csv"), [Record()], Context(), CancellationToken.None);

        capturedName.Should().NotBeNull();
        capturedName!.Should().StartWith("patients_").And.NotContain(".csv");
    }

    [Fact]
    public async Task Append_mode_strips_a_write_mode_suffix_from_the_destination_object_stem()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(
            Destination(), Mapping(destinationObject: "Patient;mode=upsert"), [Record()], Context(), CancellationToken.None);

        capturedName.Should().NotBeNull();
        capturedName!.Should().StartWith("Patient_").And.NotContain(";").And.NotContain("mode");
    }

    [Fact]
    public async Task Upsert_mode_writes_one_blob_per_record_named_by_the_upsert_key()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        var capturedNames = new List<string>();
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedNames.Add(name))
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        var records = new[]
        {
            Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" }),
            Record("p2", new Dictionary<string, object?> { ["PatientId"] = "xyz-789" }),
        };

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_writeMode":"upsert"}"""), mapping, records, Context(), CancellationToken.None);

        result.Count.Should().Be(2);
        capturedNames.Should().BeEquivalentTo(["Patient/abc-123.json", "Patient/xyz-789.json"]);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Upsert_mode_falls_back_to_SourceResourceId_when_no_upsert_key_field_is_configured()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(
            Destination("""{"dest_writeMode":"upsert"}"""), Mapping(), [Record("source-res-1")], Context(), CancellationToken.None);

        capturedName.Should().Be("Patient/source-res-1.json");
    }

    [Fact]
    public async Task Upsert_mode_uses_a_unique_name_when_no_key_is_resolvable_at_all()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        var capturedNames = new List<string>();
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedNames.Add(name))
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var records = new[]
        {
            new MappedDestinationRecord(Guid.NewGuid(), "Patient", "Patient", null, new Dictionary<string, object?> { ["Name"] = "Alice" }),
        };

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_writeMode":"upsert"}"""), Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        capturedNames.Should().ContainSingle();
        capturedNames[0].Should().MatchRegex(@"^Patient/\d{17}_[0-9a-f]{32}\.json$");
    }

    [Fact]
    public async Task Upsert_mode_uploads_a_single_records_JSON_with_json_content_type()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        BlobUploadOptions? capturedOptions = null;
        Stream? capturedStream = null;
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, BlobUploadOptions, CancellationToken>((stream, options, _) =>
            {
                capturedStream = new MemoryStream();
                stream.CopyTo(capturedStream);
                capturedStream.Position = 0;
                capturedOptions = options;
            })
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        await CreateWriter().WriteAsync(
            Destination("""{"dest_writeMode":"upsert"}"""),
            mapping,
            [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123", ["Name"] = "Alice" })],
            Context(),
            CancellationToken.None);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.HttpHeaders!.ContentType.Should().Be("application/json");
        capturedOptions.Metadata!["sourceResourceId"].Should().Be("p1");
        var content = Encoding.UTF8.GetString(((MemoryStream)capturedStream!).ToArray());
        content.Should().Contain("Alice").And.NotContain(Environment.NewLine);
    }

    [Fact]
    public async Task Upsert_mode_still_creates_the_container_when_enabled_and_supported()
    {
        SetupTarget(supportsContainerCreate: true);
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        await CreateWriter().WriteAsync(
            Destination("""{"dest_writeMode":"upsert"}"""), Mapping(fields: [UpsertKeyField()]),
            [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" })], Context(), CancellationToken.None);

        _container.Verify(
            c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Insert_mode_never_reuses_a_name_even_for_the_same_key_twice()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        var capturedNames = new List<string>();
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedNames.Add(name))
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        var records = new[]
        {
            Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" }),
            Record("p2", new Dictionary<string, object?> { ["PatientId"] = "abc-123" }), // same key, again
        };

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"insert"}"""),
            mapping, records, Context(), CancellationToken.None);

        result.Count.Should().Be(2);
        capturedNames.Should().HaveCount(2);
        capturedNames[0].Should().NotBe(capturedNames[1]); // never collides, even for the same logical key
        capturedNames.Should().OnlyContain(name => name.StartsWith("Patient/abc-123_"));
    }

    [Fact]
    public async Task Update_mode_overwrites_a_record_whose_blob_already_exists()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ExistsResponse(true));
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"update"}"""),
            mapping, [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" })],
            Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_mode_skips_a_record_whose_blob_does_not_exist_yet()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ExistsResponse(false));

        var mapping = Mapping(fields: [UpsertKeyField()]);
        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"update"}"""),
            mapping, [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" })],
            Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_mode_skips_a_record_with_no_resolvable_key_without_checking_existence()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);

        var records = new[]
        {
            new MappedDestinationRecord(Guid.NewGuid(), "Patient", "Patient", null, new Dictionary<string, object?> { ["Name"] = "Alice" }),
        };

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"update"}"""),
            Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        blob.Verify(b => b.ExistsAsync(It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Custom_FolderPattern_and_FileNamePattern_are_honored_for_Insert()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        await CreateWriter().WriteAsync(
            Destination("""
                {"dest_blobGranularity":"individual","dest_blobRecordMode":"insert",
                 "dest_blobFolderPattern":"exports/{name}","dest_blobFileNamePattern":"{id}-{date:yyyyMMdd}.json"}
                """),
            mapping, [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" })],
            Context(), CancellationToken.None);

        capturedName.Should().NotBeNull();
        Regex.IsMatch(capturedName!, @"^exports/Patient/abc-123-\d{8}\.json$").Should().BeTrue(capturedName);
    }

    [Fact]
    public async Task A_resolved_key_containing_a_slash_does_not_split_the_file_name_into_extra_folders()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        // The configured FileNamePattern itself never contains "/" (BlobDestinationSettings rejects that) —
        // this is the case where the DATA does: a resolved key value like "abc/123" (e.g. a raw FHIR reference)
        // must not be allowed to smuggle in an extra folder the way a literal "/" in the pattern would.
        var mapping = Mapping(fields: [UpsertKeyField()]);
        await CreateWriter().WriteAsync(
            Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"upsert"}"""),
            mapping, [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc/123" })],
            Context(), CancellationToken.None);

        capturedName.Should().Be("Patient/abc_123.json");
    }

    [Fact]
    public async Task A_nested_date_folder_pattern_produces_real_subfolders()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        await CreateWriter().WriteAsync(
            Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"upsert","dest_blobFolderPattern":"{name}/{date:yyyy/MM/dd}"}"""),
            mapping, [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" })],
            Context(), CancellationToken.None);

        capturedName.Should().NotBeNull();
        Regex.IsMatch(capturedName!, @"^Patient/\d{4}/\d{2}/\d{2}/abc-123\.json$").Should().BeTrue(capturedName);
    }

    [Fact]
    public async Task A_malformed_date_token_falls_back_to_a_sortable_timestamp_instead_of_throwing()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        string? capturedName = null;
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedName = name)
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        await CreateWriter().WriteAsync(
            // An unterminated literal (unmatched single quote) is a genuinely malformed .NET custom format string —
            // ToString throws FormatException for it, unlike a merely-unrecognized letter sequence.
            Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"upsert","dest_blobFileNamePattern":"{id}_{date:'unterminated}.json"}"""),
            mapping, [Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" })],
            Context(), CancellationToken.None);

        capturedName.Should().NotBeNull();
        Regex.IsMatch(capturedName!, @"^Patient/abc-123_\d{17}\.json$").Should().BeTrue(capturedName);
    }

    [Fact]
    public async Task Upsert_default_pattern_keeps_the_name_static_across_two_runs_for_the_same_key()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        var capturedNames = new List<string>();
        _container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(name => capturedNames.Add(name))
            .Returns(blob.Object);
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var mapping = Mapping(fields: [UpsertKeyField()]);
        var record = Record("p1", new Dictionary<string, object?> { ["PatientId"] = "abc-123" });
        var writer = CreateWriter();
        var destination = Destination("""{"dest_blobGranularity":"individual","dest_blobRecordMode":"upsert"}""");

        await writer.WriteAsync(destination, mapping, [record], Context(), CancellationToken.None);
        await writer.WriteAsync(destination, mapping, [record], Context(), CancellationToken.None);

        capturedNames.Should().HaveCount(2);
        capturedNames[0].Should().Be(capturedNames[1]);
    }

    [Fact]
    public async Task Bulk_granularity_is_the_default_when_nothing_is_configured()
    {
        SetupTarget();
        var blob = new Mock<BlobClient>();
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        _container
            .Setup(c => c.CreateIfNotExistsAsync(It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateContainerResponse());
        blob
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse());

        var result = await CreateWriter().WriteAsync(Destination(), Mapping(), [Record(), Record("p2")], Context(), CancellationToken.None);

        result.Count.Should().Be(2); // both records landed in the same bulk blob
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
