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

    private static MappingProfile Mapping() =>
        new("Patient Blob", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []);

    private static MappedDestinationRecord Record(string id = "p1") =>
        new(Guid.NewGuid(), "Patient", "Patient", id, new Dictionary<string, object?> { ["Name"] = "Alice" });

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
}
