using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using RuntimeDestinationWriteResult = FHIRBridge.Runtime.Application.Workflows.Payloads.DestinationWriteResult;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// The two lake destinations (Data Lake Webhook, Microsoft Fabric/OneLake) sit in the same position Blob Storage
/// does with respect to the base <see cref="DestinationNodeExecutor"/>: neither writer groups a mixed batch by
/// resource type internally, and both derive something user-visible from <c>MappingProfile.DestinationObject</c>
/// (a OneLake file path and partition folder; a webhook routing header and idempotency key). So both must get a
/// mixed batch pre-split per resource type, and neither may receive the SQL-only ";mode=..." write-mode suffix.
/// These tests lock in both behaviors, which are configured — and therefore silently breakable — in
/// DestinationNodeExecutors' static sets rather than in either writer.
/// </summary>
public sealed class LakeDestinationNodeExecutorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(DestinationType.DataLakeWebhook)]
    [InlineData(DestinationType.DataFabricAzure)]
    public async Task A_mixed_batch_is_split_so_each_resource_type_is_written_under_its_own_profile(
        DestinationType destinationType)
    {
        var (executor, capturedCalls) = CreateExecutor(destinationType, writeMode: null);

        var output = await executor.ExecuteAsync(
            CreateContext(), CreateNode(destinationType, writeMode: null), [MixedUpstreamBatch()], CancellationToken.None);

        capturedCalls.Should().HaveCount(
            2, "each resource type must reach the writer in its own call, or the file path / routing header would "
                + "label every record as whichever single type the profile happened to carry");

        var patientCall = capturedCalls.Single(call => call.Profile.ResourceType == "Patient");
        patientCall.Records.Should().ContainSingle(record => record.SourceResourceId == "p1");

        var observationCall = capturedCalls.Single(call => call.Profile.ResourceType == "Observation");
        observationCall.Records.Should().ContainSingle(record => record.SourceResourceId == "o1");

        ((RuntimeDestinationWriteResult)output.Payload).RecordsWritten.Should().Be(2);
    }

    [Theory]
    [InlineData(DestinationType.DataLakeWebhook)]
    [InlineData(DestinationType.DataFabricAzure)]
    public async Task The_sql_only_write_mode_suffix_never_reaches_a_lake_destinations_profile(
        DestinationType destinationType)
    {
        var (executor, capturedCalls) = CreateExecutor(destinationType, writeMode: "upsert");

        await executor.ExecuteAsync(
            CreateContext(), CreateNode(destinationType, writeMode: "upsert"), [MixedUpstreamBatch()], CancellationToken.None);

        capturedCalls.Should().NotBeEmpty();
        capturedCalls.Select(call => call.Profile.DestinationObject)
            .Should().OnlyContain(destinationObject => !destinationObject.Contains(';'),
                "a \";mode=...\" suffix would end up in the OneLake file name and in the webhook's idempotency key, "
                    + "which has to stay byte-stable across re-runs to be worth anything");
        capturedCalls.Select(call => call.Profile.DestinationObject)
            .Should().BeEquivalentTo(["Patient", "Observation"]);
    }

    // ---------------------------------------------------------------- helpers

    private static (DestinationNodeExecutor Executor,
        List<(MappingProfile Profile, IReadOnlyCollection<MappedDestinationRecord> Records)> CapturedCalls)
        CreateExecutor(DestinationType destinationType, string? writeMode)
    {
        var destinationId = DestinationIdFor(destinationType);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Profile("Patient", destinationId),
                Profile("Observation", destinationId),
            ]);

        var capturedCalls = new List<(MappingProfile, IReadOnlyCollection<MappedDestinationRecord>)>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer
            .Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(),
                It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(),
                It.IsAny<PipelineWriteContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, profile, records, _, _) => capturedCalls.Add((profile, records)))
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(destinationType)).Returns(writer.Object);

        DestinationNodeExecutor executor = destinationType == DestinationType.DataLakeWebhook
            ? new DataLakeWebhookDestinationNodeExecutor(writerFactory.Object, configurationRepository: repository.Object)
            : new DataFabricAzureDestinationNodeExecutor(writerFactory.Object, configurationRepository: repository.Object);

        return (executor, capturedCalls);
    }

    // Stable per type so the node's destinationId and its profiles' DestinationConfigurationId agree.
    private static Guid DestinationIdFor(DestinationType destinationType) => destinationType switch
    {
        DestinationType.DataLakeWebhook => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        _ => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
    };

    private static MappingProfile Profile(string resourceType, Guid destinationId) =>
        new(
            resourceType,
            resourceType,
            Guid.NewGuid(),
            destinationId,
            resourceType,
            [new MappingField($"{resourceType}Id", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

    private static WorkflowNodeOutput MixedUpstreamBatch()
    {
        var patient = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });
        var observation = new MappedDestinationRecord(
            Guid.NewGuid(), "Observation", "Observation", "o1", new Dictionary<string, object?> { ["ObservationId"] = "o1" });

        return new WorkflowNodeOutput(
            Guid.NewGuid(),
            WorkflowNodeTypes.Mapping,
            new MappedRecordBatch([patient, observation]),
            WorkflowDataContract.MappedRecordBatch);
    }

    private static WorkflowNode CreateNode(DestinationType destinationType, string? writeMode)
    {
        var config = new Dictionary<string, object>
        {
            ["destinationId"] = DestinationIdFor(destinationType).ToString(),
            ["secretKeyVaultName"] = "workflow-secrets",
            ["secretName"] = "dest-test",
        };
        if (writeMode is not null)
        {
            config["dest_writeMode"] = writeMode;
        }

        var nodeType = destinationType == DestinationType.DataLakeWebhook
            ? WorkflowNodeTypes.DataLakeWebhookDestination
            : WorkflowNodeTypes.DataFabricAzureDestination;

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "lake-destination-node-test", 1);
        return workflow.AddNode(
            nodeType,
            WorkflowNodeCategory.Destination,
            90,
            configurationJson: JsonSerializer.Serialize(config, JsonOptions));
    }

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test-correlation");
}
