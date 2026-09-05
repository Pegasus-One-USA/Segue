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
/// Covers the fix for a real production bug: one Destination node writing a batch that spans multiple resource
/// types (e.g. Patient AND Observation, once MappingNodeExecutor started mapping each resource type through its
/// own profile) used to resolve a single MappingProfile for the WHOLE batch, so only whichever resource type
/// that one profile matched ever reached its table — every other resource type's records were silently handed
/// to the writer under the wrong profile/table. Each resource type must resolve and write against its own
/// MappingProfile.
/// </summary>
public sealed class DestinationNodeExecutorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Records_for_different_resource_types_are_written_using_each_ones_own_mapping_profile()
    {
        var destinationId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        var observationProfile = new MappingProfile(
            "Observation", "Observation", Guid.NewGuid(), destinationId, "Observation",
            [new MappingField("ObservationId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([patientProfile, observationProfile]);

        var capturedCalls = new List<(MappingProfile Profile, IReadOnlyCollection<MappedDestinationRecord> Records)>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, profile, records, _, _) => capturedCalls.Add((profile, records)))
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.SqlServer)).Returns(writer.Object);

        var executor = new SqlServerDestinationNodeExecutor(writerFactory.Object, configurationRepository: repository.Object);
        var node = CreateDestinationNode(destinationId);

        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });
        var observationRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Observation", "Observation", "o1", new Dictionary<string, object?> { ["ObservationId"] = "o1" });
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping,
            new MappedRecordBatch([patientRecord, observationRecord]), WorkflowDataContract.MappedRecordBatch);

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        capturedCalls.Should().HaveCount(2, "each resource type must be written with its own separate WriteAsync call");

        var patientCall = capturedCalls.Single(c => c.Profile.ResourceType == "Patient");
        patientCall.Profile.DestinationObject.Should().Be("Patient");
        patientCall.Records.Should().ContainSingle(r => r.SourceResourceId == "p1");

        var observationCall = capturedCalls.Single(c => c.Profile.ResourceType == "Observation");
        observationCall.Profile.DestinationObject.Should().Be("Observation");
        observationCall.Records.Should().ContainSingle(r => r.SourceResourceId == "o1");

        var result = (RuntimeDestinationWriteResult)output.Payload!;
        result.RecordsWritten.Should().Be(2, "both records were written, just via two separate calls");
    }

    /// <summary>
    /// Regression test for a real production incident: fetching each resource type's REAL MappingProfile (a
    /// clean "Patient"/"Observation" DestinationObject, no mode info) silently broke Upsert — the only place
    /// "mode=upsert" ever lived was smuggled inside the legacy synthetic profile's compound DestinationObject
    /// ("dbo.Patient;mode=upsert"), which real per-resource-type profile resolution doesn't produce. Without
    /// re-splicing the destination's own "dest_writeMode" config back onto each group's profile,
    /// MappedSqlServerDestinationWriter's ParseDestinationTarget silently defaults to Insert — Upsert then
    /// duplicates every re-run instead of updating in place, for every resource type on the destination.
    /// </summary>
    [Fact]
    public async Task The_destinations_configured_write_mode_is_applied_to_every_resource_types_resolved_profile()
    {
        var destinationId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        var observationProfile = new MappingProfile(
            "Observation", "Observation", Guid.NewGuid(), destinationId, "Observation",
            [new MappingField("ObservationId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([patientProfile, observationProfile]);

        var capturedProfiles = new List<MappingProfile>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, profile, _, _, _) => capturedProfiles.Add(profile))
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.SqlServer)).Returns(writer.Object);

        var executor = new SqlServerDestinationNodeExecutor(writerFactory.Object, configurationRepository: repository.Object);
        var node = CreateDestinationNode(destinationId, writeMode: "upsert");

        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });
        var observationRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Observation", "Observation", "o1", new Dictionary<string, object?> { ["ObservationId"] = "o1" });
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping,
            new MappedRecordBatch([patientRecord, observationRecord]), WorkflowDataContract.MappedRecordBatch);

        await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        capturedProfiles.Should().HaveCount(2);
        capturedProfiles.Single(p => p.ResourceType == "Patient").DestinationObject.Should().Be("Patient;mode=upsert");
        capturedProfiles.Single(p => p.ResourceType == "Observation").DestinationObject.Should().Be("Observation;mode=upsert");
    }

    /// <summary>
    /// Regression test for a real production bug: two workflows shared the same (DestinationId, ResourceType)
    /// pair — resolving "the" profile by that shared pair (even with a most-recently-modified tie-break) let
    /// one workflow's save silently pick up (and, on its own next save, overwrite) a DIFFERENT workflow's
    /// profile, eventually throwing "Invalid column name" once the two profiles' shapes diverged. The fix:
    /// resolve strictly by THIS node's own mappingProfileIds — an id it saved itself — never by searching
    /// every profile sharing the same destination + resource type.
    /// </summary>
    [Fact]
    public async Task Resolves_the_profile_by_this_nodes_own_mappingProfileIds_never_by_searching_profiles_sharing_the_same_destination_and_resource_type()
    {
        var destinationId = Guid.NewGuid();
        var otherWorkflowsProfile = new MappingProfile(
            "Patient (other workflow)", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("Name", "$.name[*].text", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        otherWorkflowsProfile.ApplyModified(null, new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc));

        var thisWorkflowsProfile = new MappingProfile(
            "Patient (this workflow)", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        thisWorkflowsProfile.ApplyModified(null, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(thisWorkflowsProfile.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(thisWorkflowsProfile);

        MappingProfile? capturedProfile = null;
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, profile, _, _, _) => capturedProfile = profile)
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.SqlServer)).Returns(writer.Object);

        var executor = new SqlServerDestinationNodeExecutor(writerFactory.Object, configurationRepository: repository.Object);
        var node = CreateDestinationNode(
            destinationId, mappingProfileIds: new Dictionary<string, string> { ["Patient"] = thisWorkflowsProfile.Id.ToString() });
        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping, new MappedRecordBatch([patientRecord]), WorkflowDataContract.MappedRecordBatch);

        await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        capturedProfile.Should().NotBeNull();
        capturedProfile!.Name.Should().Be("Patient (this workflow)");
        repository.Verify(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "resolution must never search across every profile sharing this destination + resource type — that search is exactly what let one workflow's save leak into another's");
        repository.Verify(r => r.FindMappingProfileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "resolution must never search by the (resourceType, sourceConnectionId, destinationId) triple — more than one workflow can share it");
    }

    [Fact]
    public async Task Records_for_resource_types_not_selected_on_the_destination_are_filtered_out_before_writing()
    {
        // The destination selected only Patient + Condition (dest_resources), but the upstream batch also carries a
        // Binary record (e.g. a bulk source that returned a referenced type beyond the requested _type). Only the
        // selected types must reach the writer; the unselected one is dropped and surfaced in node metadata.
        var destinationId = Guid.NewGuid();

        IReadOnlyCollection<MappedDestinationRecord>? writtenRecords = null;
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, _, records, _, _) => writtenRecords = records)
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.Medplum)).Returns(writer.Object);

        var executor = new MedplumDestinationNodeExecutor(writerFactory.Object);
        var node = CreateMedplumNode(destinationId, resourceSelection: "Patient,Condition");

        var records = new[]
        {
            new MappedDestinationRecord(Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?>(), "{\"resourceType\":\"Patient\",\"id\":\"p1\"}"),
            new MappedDestinationRecord(Guid.NewGuid(), "Condition", "Condition", "c1", new Dictionary<string, object?>(), "{\"resourceType\":\"Condition\",\"id\":\"c1\"}"),
            new MappedDestinationRecord(Guid.NewGuid(), "Binary", "Binary", "b1", new Dictionary<string, object?>(), "{\"resourceType\":\"Binary\",\"id\":\"b1\"}"),
        };
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping, new MappedRecordBatch(records), WorkflowDataContract.MappedRecordBatch);

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        writtenRecords.Should().NotBeNull();
        writtenRecords!.Select(r => r.ResourceType).Should().BeEquivalentTo(["Patient", "Condition"], "the unselected Binary record must be filtered out before the write");
        output.Metadata.Should().ContainKey("filteredOutResourceTypes");
        ((string[])output.Metadata["filteredOutResourceTypes"]!).Should().BeEquivalentTo(["Binary"]);
    }

    [Fact]
    public async Task All_records_are_written_when_the_destination_declares_no_resource_selection()
    {
        // Guard: a destination node without a dest_resources/dest_targets selection must not filter anything.
        var destinationId = Guid.NewGuid();

        IReadOnlyCollection<MappedDestinationRecord>? writtenRecords = null;
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, _, records, _, _) => writtenRecords = records)
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.Medplum)).Returns(writer.Object);

        var executor = new MedplumDestinationNodeExecutor(writerFactory.Object);
        var node = CreateMedplumNode(destinationId, resourceSelection: null);

        var records = new[]
        {
            new MappedDestinationRecord(Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?>(), "{\"resourceType\":\"Patient\",\"id\":\"p1\"}"),
            new MappedDestinationRecord(Guid.NewGuid(), "Binary", "Binary", "b1", new Dictionary<string, object?>(), "{\"resourceType\":\"Binary\",\"id\":\"b1\"}"),
        };
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping, new MappedRecordBatch(records), WorkflowDataContract.MappedRecordBatch);

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        writtenRecords.Should().NotBeNull();
        writtenRecords!.Select(r => r.ResourceType).Should().BeEquivalentTo(["Patient", "Binary"]);
        output.Metadata["filteredOutResourceTypes"].Should().BeNull();
    }

    private static WorkflowNode CreateMedplumNode(Guid destinationId, string? resourceSelection)
    {
        var config = new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
            ["secretKeyVaultName"] = "workflow-secrets",
            ["secretName"] = "dest-test",
            ["dest_medplumBaseUrl"] = "https://api.medplum.com/fhir/R4",
            ["dest_medplumClientId"] = "client-1",
            ["dest_medplumAuthMethod"] = "client_secret",
        };
        if (resourceSelection is not null)
        {
            config["dest_resources"] = resourceSelection;
        }

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "medplum-destination-node-test", 1);
        return workflow.AddNode(
            WorkflowNodeTypes.MedplumDestination,
            WorkflowNodeCategory.Destination,
            90,
            configurationJson: JsonSerializer.Serialize(config, JsonOptions));
    }

    private static WorkflowNode CreateDestinationNode(
        Guid destinationId, string? writeMode = null, Guid? sourceConnectionId = null,
        IReadOnlyDictionary<string, string>? mappingProfileIds = null)
    {
        var config = new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
            ["secretKeyVaultName"] = "workflow-secrets",
            ["secretName"] = "dest-test",
        };
        if (writeMode is not null)
        {
            config["dest_writeMode"] = writeMode;
        }

        if (sourceConnectionId is not null)
        {
            config["sourceConnectionId"] = sourceConnectionId.ToString()!;
        }

        if (mappingProfileIds is not null)
        {
            config["mappingProfileIds"] = mappingProfileIds;
        }

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "destination-node-test", 1);
        return workflow.AddNode(
            WorkflowNodeTypes.SqlServerDestination,
            WorkflowNodeCategory.Destination,
            90,
            configurationJson: JsonSerializer.Serialize(config, JsonOptions));
    }

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test-correlation");
}
