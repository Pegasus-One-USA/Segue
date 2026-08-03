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
    /// Regression test for a real production bug: two MappingProfiles existed for the same (DestinationId,
    /// ResourceType) pair — a stale one left behind by an earlier/abandoned wizard save (missing a resolvable
    /// "$.id" field entirely) alongside the current correct one. Picking whichever GetMappingProfilesAsync
    /// happened to return first threw "Invalid column name 'SourceResourceId'" when the stale profile won.
    /// Must always prefer the most recently modified match.
    /// </summary>
    [Fact]
    public async Task When_multiple_profiles_match_the_same_destination_and_resource_type_the_most_recently_modified_one_wins()
    {
        var destinationId = Guid.NewGuid();
        var staleProfile = new MappingProfile(
            "Patient (stale)", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("Name", "$.name[*].text", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        staleProfile.ApplyModified(null, new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc));

        var currentProfile = new MappingProfile(
            "Patient (current)", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        currentProfile.ApplyModified(null, new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc));

        var repository = new Mock<IConfigurationRepository>();
        // Deliberately returned stale-first — the fix must not depend on incidental query order.
        repository.Setup(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([staleProfile, currentProfile]);

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
        var node = CreateDestinationNode(destinationId);
        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping, new MappedRecordBatch([patientRecord]), WorkflowDataContract.MappedRecordBatch);

        await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        capturedProfile.Should().NotBeNull();
        capturedProfile!.Name.Should().Be("Patient (current)");
    }

    /// <summary>
    /// When the destination node config carries sourceConnectionId (stamped by /workflows/build alongside
    /// destinationId), the exact natural-key match (ResourceType, SourceConnectionId, DestinationId) — the same
    /// key MappingNodeExecutor and MappingImportService de-dup on — must win outright, without even needing the
    /// most-recently-modified tie-break: a stale profile sharing the same DestinationId + ResourceType but a
    /// DIFFERENT SourceConnectionId must never be picked, no matter how recently it was touched.
    /// </summary>
    [Fact]
    public async Task An_exact_natural_key_match_wins_over_a_more_recently_modified_but_wrong_source_connection_profile()
    {
        var destinationId = Guid.NewGuid();
        var thisWorkflowsSourceConnectionId = Guid.NewGuid();

        var wrongConnectionButRecentlyModified = new MappingProfile(
            "Patient (other workflow)", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("Name", "$.name[*].text", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        wrongConnectionButRecentlyModified.ApplyModified(null, new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc));

        var correctProfile = new MappingProfile(
            "Patient (this workflow)", "Patient", thisWorkflowsSourceConnectionId, destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        correctProfile.ApplyModified(null, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.FindMappingProfileAsync("Patient", thisWorkflowsSourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(correctProfile);
        repository.Setup(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([wrongConnectionButRecentlyModified, correctProfile]);

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
        var node = CreateDestinationNode(destinationId, sourceConnectionId: thisWorkflowsSourceConnectionId);
        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping, new MappedRecordBatch([patientRecord]), WorkflowDataContract.MappedRecordBatch);

        await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        capturedProfile.Should().NotBeNull();
        capturedProfile!.Name.Should().Be("Patient (this workflow)");
        repository.Verify(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "the exact natural-key match succeeded, so the ambiguous DestinationId-only fallback must never even run");
    }

    private static WorkflowNode CreateDestinationNode(Guid destinationId, string? writeMode = null, Guid? sourceConnectionId = null)
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

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "destination-node-test", 1);
        return workflow.AddNode(
            WorkflowNodeTypes.SqlServerDestination,
            WorkflowNodeCategory.Destination,
            90,
            configurationJson: JsonSerializer.Serialize(config, JsonOptions));
    }

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test-correlation");
}
