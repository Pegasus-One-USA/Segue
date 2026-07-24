using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// The CSV writer's only job is to serialize records to CSV once and dispatch to whichever
/// <see cref="ArtifactDeliveryMode"/> the destination's <c>dest_deliveryMode</c> metadata field selects — these
/// cover that dispatch, including the legacy-row default (no field present) falling back to Download.
/// </summary>
public sealed class MappedCsvDestinationWriterTests
{
    private static DestinationConfiguration Destination(string? connectionMetadataJson) =>
        new("CSV Export", DestinationType.Csv, new SecretReference("kv", "secret"), "export", connectionMetadataJson);

    private static MappingProfile Mapping() =>
        new("Patient CSV", "Patient", Guid.NewGuid(), Guid.NewGuid(), "patients.csv", []);

    private static MappedDestinationRecord Record() =>
        new(Guid.NewGuid(), "Patient", "patients.csv", "123", new Dictionary<string, object?> { ["Name"] = "Alice" });

    private static PipelineWriteContext Context(bool allowInline = true) =>
        new(allowInline, "Route", DateTimeOffset.UtcNow);

    private static (MappedCsvDestinationWriter Writer, Mock<IArtifactDeliveryStrategyFactory> Factory, Mock<IArtifactDeliveryStrategy> Strategy)
        CreateWriter()
    {
        var strategy = new Mock<IArtifactDeliveryStrategy>();
        strategy
            .Setup(s => s.DeliverAsync(
                It.IsAny<DestinationConfiguration>(),
                It.IsAny<GeneratedFile>(),
                It.IsAny<int>(),
                It.IsAny<PipelineWriteContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationWriteResult(1));

        var factory = new Mock<IArtifactDeliveryStrategyFactory>();
        factory.Setup(f => f.Create(It.IsAny<ArtifactDeliveryMode>())).Returns(strategy.Object);

        return (new MappedCsvDestinationWriter(factory.Object), factory, strategy);
    }

    [Theory]
    [InlineData("""{"dest_deliveryMode":"email"}""", ArtifactDeliveryMode.Email)]
    [InlineData("""{"dest_deliveryMode":"sftp"}""", ArtifactDeliveryMode.Sftp)]
    [InlineData("""{"dest_deliveryMode":"downloadUrl"}""", ArtifactDeliveryMode.DownloadUrl)]
    [InlineData("""{"dest_deliveryMode":"download"}""", ArtifactDeliveryMode.Download)]
    public async Task Dispatches_to_the_strategy_selected_by_dest_deliveryMode(string metadataJson, ArtifactDeliveryMode expectedMode)
    {
        var (writer, factory, _) = CreateWriter();

        await writer.WriteAsync(Destination(metadataJson), Mapping(), [Record()], Context(), CancellationToken.None);

        factory.Verify(f => f.Create(expectedMode), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"dest_folder":"/exports"}""")]
    public async Task Legacy_rows_missing_dest_deliveryMode_default_to_Download(string? metadataJson)
    {
        var (writer, factory, _) = CreateWriter();

        await writer.WriteAsync(Destination(metadataJson), Mapping(), [Record()], Context(), CancellationToken.None);

        factory.Verify(f => f.Create(ArtifactDeliveryMode.Download), Times.Once);
    }

    [Fact]
    public async Task Builds_a_real_CSV_GeneratedFile_and_passes_the_record_count_through()
    {
        var (writer, _, strategy) = CreateWriter();
        var records = new[] { Record(), Record() };

        var result = await writer.WriteAsync(
            Destination("""{"dest_deliveryMode":"download"}"""), Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(1); // the mocked strategy's own return value flows straight back
        strategy.Verify(s => s.DeliverAsync(
            It.IsAny<DestinationConfiguration>(),
            It.Is<GeneratedFile>(f => f.ContentType == "text/csv" && f.Content.Length > 0),
            2,
            It.IsAny<PipelineWriteContext>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Empty_record_set_short_circuits_without_invoking_any_delivery_strategy()
    {
        var (writer, factory, _) = CreateWriter();

        var result = await writer.WriteAsync(
            Destination("""{"dest_deliveryMode":"email"}"""), Mapping(), [], Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        factory.Verify(f => f.Create(It.IsAny<ArtifactDeliveryMode>()), Times.Never);
    }
}
