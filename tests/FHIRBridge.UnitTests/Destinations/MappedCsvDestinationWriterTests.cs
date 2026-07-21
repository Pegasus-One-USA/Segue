using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
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
/// The CSV writer groups records by FHIR resource type and serializes only the mapped fields (no lineage columns)
/// per resource type. A single selected resource is delivered as a plain CSV; two or more are bundled into a single
/// ZIP archive (named after the workflow), one CSV entry per resource type. Either way, dispatches to whichever
/// <see cref="ArtifactDeliveryMode"/> the destination's <c>dest_deliveryMode</c> metadata field selects.
/// </summary>
public sealed class MappedCsvDestinationWriterTests
{
    private static DestinationConfiguration Destination(string? connectionMetadataJson) =>
        new("CSV Export", DestinationType.Csv, new SecretReference("kv", "secret"), "export", connectionMetadataJson);

    private static MappingProfile Mapping() =>
        new("Patient CSV", "Patient", Guid.NewGuid(), Guid.NewGuid(), "patients.csv", []);

    private static MappedDestinationRecord Record() =>
        new(Guid.NewGuid(), "Patient", "patients.csv", "123", new Dictionary<string, object?> { ["Name"] = "Alice" });

    private const string WorkflowName = "Patient Export Workflow";

    private static PipelineWriteContext Context(bool allowInline = true) =>
        new(allowInline, WorkflowName, DateTimeOffset.UtcNow);

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
    public async Task Empty_record_set_short_circuits_without_invoking_any_delivery_strategy()
    {
        var (writer, factory, _) = CreateWriter();

        var result = await writer.WriteAsync(
            Destination("""{"dest_deliveryMode":"email"}"""), Mapping(), [], Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        factory.Verify(f => f.Create(It.IsAny<ArtifactDeliveryMode>()), Times.Never);
    }

    // Matches "{ResourceType}_{yyyyMMdd_HHmmss}.csv", e.g. "Patient_20260721_143022.csv".
    private static readonly Regex CsvEntryNamePattern = new(@"^[A-Za-z]+_\d{8}_\d{6}\.csv$");

    [Fact]
    public async Task Single_selected_resource_is_delivered_as_a_plain_CSV_not_a_zip()
    {
        var (writer, _, strategy) = CreateWriter();
        GeneratedFile? capturedFile = null;
        strategy
            .Setup(s => s.DeliverAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<GeneratedFile>(), It.IsAny<int>(),
                It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, GeneratedFile, int, PipelineWriteContext, CancellationToken>(
                (_, file, _, _, _) => capturedFile = file)
            .ReturnsAsync(new DestinationWriteResult(1));
        var records = new[] { Record(), Record() }; // both "Patient" — one resource type

        var result = await writer.WriteAsync(
            Destination("""{"dest_deliveryMode":"download"}"""), Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(1); // the mocked strategy's own return value flows straight back
        capturedFile.Should().NotBeNull();
        capturedFile!.ContentType.Should().Be("text/csv");
        CsvEntryNamePattern.IsMatch(capturedFile.FileName).Should().BeTrue();
        capturedFile.FileName.Should().StartWith("Patient_");
        capturedFile.Content.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Multiple_selected_resources_are_bundled_into_one_ZIP_named_after_the_workflow()
    {
        var (writer, _, strategy) = CreateWriter();
        GeneratedFile? capturedFile = null;
        strategy
            .Setup(s => s.DeliverAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<GeneratedFile>(), It.IsAny<int>(),
                It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, GeneratedFile, int, PipelineWriteContext, CancellationToken>(
                (_, file, _, _, _) => capturedFile = file)
            .ReturnsAsync(new DestinationWriteResult(1));

        var records = new[]
        {
            new MappedDestinationRecord(Guid.NewGuid(), "Patient", "patients.csv", "p1", new Dictionary<string, object?> { ["Name"] = "Alice" }),
            new MappedDestinationRecord(Guid.NewGuid(), "Patient", "patients.csv", "p2", new Dictionary<string, object?> { ["Name"] = "Bob" }),
            new MappedDestinationRecord(Guid.NewGuid(), "Observation", "observations.csv", "o1", new Dictionary<string, object?> { ["Code"] = "8302-2" }),
            new MappedDestinationRecord(Guid.NewGuid(), "Condition", "conditions.csv", "c1", new Dictionary<string, object?> { ["Code"] = "E11.9" }),
        };

        var result = await writer.WriteAsync(
            Destination("""{"dest_deliveryMode":"download"}"""), Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        capturedFile.Should().NotBeNull();
        capturedFile!.ContentType.Should().Be("application/zip");
        capturedFile.FileName.Should().MatchRegex(@"^Patient_Export_Workflow_\d{8}_\d{6}\.zip$");

        using var archive = new ZipArchive(new MemoryStream(capturedFile.Content), ZipArchiveMode.Read);

        archive.Entries.Select(e => e.Name).Should().HaveCount(3);
        archive.Entries.Select(e => e.Name).Should().OnlyContain(name => CsvEntryNamePattern.IsMatch(name));
        archive.Entries.Select(e => e.Name.Split('_')[0]).Should().BeEquivalentTo(["Patient", "Observation", "Condition"]);

        var patientEntry = archive.Entries.Single(e => e.Name.StartsWith("Patient_"));
        using var reader = new StreamReader(patientEntry.Open(), Encoding.UTF8);
        var patientCsv = await reader.ReadToEndAsync();
        patientCsv.Should().Contain("Alice").And.Contain("Bob").And.NotContain("8302-2");
    }

    [Fact]
    public async Task CSV_contains_only_mapped_columns_no_lineage_or_system_columns()
    {
        var (writer, _, strategy) = CreateWriter();
        GeneratedFile? capturedFile = null;
        strategy
            .Setup(s => s.DeliverAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<GeneratedFile>(), It.IsAny<int>(),
                It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, GeneratedFile, int, PipelineWriteContext, CancellationToken>(
                (_, file, _, _, _) => capturedFile = file)
            .ReturnsAsync(new DestinationWriteResult(1));

        var records = new[]
        {
            new MappedDestinationRecord(Guid.NewGuid(), "Patient", "patients.csv", "p1",
                new Dictionary<string, object?> { ["Name"] = "Alice", ["BirthDate"] = "1990-01-01" }),
        };

        await writer.WriteAsync(
            Destination("""{"dest_deliveryMode":"download"}"""), Mapping(), records, Context(), CancellationToken.None);

        capturedFile.Should().NotBeNull();
        var csv = Encoding.UTF8.GetString(capturedFile!.Content);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        header.Should().Be("BirthDate,Name"); // alphabetical, mapped columns only
        header.Should().NotContain("PipelineRunId");
        header.Should().NotContain("ResourceType");
        header.Should().NotContain("DestinationObject");
        header.Should().NotContain("SourceResourceId");
        header.Should().NotContain("WrittenOnUtc");
    }
}
