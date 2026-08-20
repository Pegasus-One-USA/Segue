using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
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

    /// <summary>
    /// Confirms the ordering documented on OrderGroupsByReferenceDependency: a group whose records reference
    /// another group's table via ReferenceLookups (e.g. Observation.PatientId resolved from "$.subject.reference"
    /// against Patient) must be written AFTER the referenced group, regardless of the order resource types
    /// happened to appear in the upstream batch — MappedSqlServerDestinationWriter's lookup query requires the
    /// referenced row to already exist in the destination table.
    /// </summary>
    [Fact]
    public async Task A_group_referencing_another_via_ReferenceLookups_is_written_after_the_referenced_group()
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

        var writeOrder = new List<string>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, profile, _, _, _) => writeOrder.Add(profile.ResourceType))
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.SqlServer)).Returns(writer.Object);

        var executor = new SqlServerDestinationNodeExecutor(writerFactory.Object, configurationRepository: repository.Object);
        var node = CreateDestinationNode(destinationId);

        // Observation references Patient via subject.reference, resolved at mapping time into a
        // ReferenceLookup pointing at the bare "Patient" table.
        var observationRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Observation", "Observation", "o1",
            new Dictionary<string, object?> { ["ObservationId"] = "o1", ["PatientId"] = null },
            ReferenceLookups: [new MappedReferenceLookup("PatientId", "Patient", "PatientId", "p1")]);
        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });

        // Deliberately Observation-then-Patient in the input batch, so a passing assertion proves real
        // reordering happened rather than the sort coincidentally matching input order.
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping,
            new MappedRecordBatch([observationRecord, patientRecord]), WorkflowDataContract.MappedRecordBatch);

        await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        writeOrder.Should().Equal(["Patient", "Observation"],
            "Patient's row must exist before Observation's reference-lookup write attempts to resolve against it");
    }

    /// <summary>
    /// Same guarantee as above, but a 3-level chain (DiagnosticReport references both Patient and Observation;
    /// Observation itself also references Patient) with the input batch in the worst-case reverse order —
    /// covers a resource with more than one required parent, and a parent that's itself a child of another.
    /// </summary>
    [Fact]
    public async Task A_three_level_reference_chain_is_written_in_dependency_order_regardless_of_input_order()
    {
        var destinationId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        var observationProfile = new MappingProfile(
            "Observation", "Observation", Guid.NewGuid(), destinationId, "Observation",
            [new MappingField("ObservationId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        var diagnosticReportProfile = new MappingProfile(
            "DiagnosticReport", "DiagnosticReport", Guid.NewGuid(), destinationId, "DiagnosticReport",
            [new MappingField("DiagnosticReportId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([patientProfile, observationProfile, diagnosticReportProfile]);

        var writeOrder = new List<string>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, profile, _, _, _) => writeOrder.Add(profile.ResourceType))
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.SqlServer)).Returns(writer.Object);

        var executor = new SqlServerDestinationNodeExecutor(writerFactory.Object, configurationRepository: repository.Object);
        var node = CreateDestinationNode(destinationId);

        var diagnosticReportRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "DiagnosticReport", "DiagnosticReport", "d1",
            new Dictionary<string, object?> { ["DiagnosticReportId"] = "d1" },
            ReferenceLookups: [
                new MappedReferenceLookup("PatientId", "Patient", "PatientId", "p1"),
                new MappedReferenceLookup("ObservationId", "Observation", "ObservationId", "o1"),
            ]);
        var observationRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Observation", "Observation", "o1",
            new Dictionary<string, object?> { ["ObservationId"] = "o1" },
            ReferenceLookups: [new MappedReferenceLookup("PatientId", "Patient", "PatientId", "p1")]);
        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1", new Dictionary<string, object?> { ["PatientId"] = "p1" });

        // Worst-case reverse input order: the most-dependent resource first, the least-dependent last.
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping,
            new MappedRecordBatch([diagnosticReportRecord, observationRecord, patientRecord]), WorkflowDataContract.MappedRecordBatch);

        await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        writeOrder.Should().Equal(["Patient", "Observation", "DiagnosticReport"],
            "each resource must be written only after every table it references via ReferenceLookups already has its rows");
    }

    /// <summary>
    /// A circular reference (Patient references Encounter AND Encounter references Patient) has no order that
    /// guarantees every reference resolves — OrderGroupsByReferenceDependency falls back to original batch order
    /// for the groups involved rather than looping forever. Confirms that fallback is reported via
    /// IGlobalExceptionManager.CaptureExpectedAsync (Informational severity, the same mechanism
    /// SourceNodeExecutors already uses for other non-fatal conditions, and the one that actually surfaces on the
    /// Operations → Errors screen / Correlation Search) rather than failing silently, and that both resource
    /// types still get written despite the cycle.
    /// </summary>
    [Fact]
    public async Task A_circular_reference_is_reported_via_the_exception_manager_instead_of_failing_silently()
    {
        var destinationId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", Guid.NewGuid(), destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);
        var encounterProfile = new MappingProfile(
            "Encounter", "Encounter", Guid.NewGuid(), destinationId, "Encounter",
            [new MappingField("EncounterId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([patientProfile, encounterProfile]);

        var writeOrder = new List<string>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, profile, _, _, _) => writeOrder.Add(profile.ResourceType))
            .ReturnsAsync((DestinationConfiguration _, MappingProfile _, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext _, CancellationToken _)
                => new FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult(records.Count));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.SqlServer)).Returns(writer.Object);

        ExpectedFailure? capturedFailure = null;
        ExceptionContext? capturedContext = null;
        var exceptionManager = new Mock<IGlobalExceptionManager>();
        exceptionManager.Setup(m => m.CaptureExpectedAsync(It.IsAny<ExpectedFailure>(), It.IsAny<ExceptionContext>(), It.IsAny<CancellationToken>()))
            .Callback<ExpectedFailure, ExceptionContext, CancellationToken>((failure, ctx, _) => { capturedFailure = failure; capturedContext = ctx; })
            .ReturnsAsync("error-ref-1");

        var executor = new SqlServerDestinationNodeExecutor(
            writerFactory.Object, configurationRepository: repository.Object, exceptionManager: exceptionManager.Object);
        var node = CreateDestinationNode(destinationId);

        // Patient references Encounter, and Encounter references Patient right back — an actual cycle.
        var patientRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Patient", "Patient", "p1",
            new Dictionary<string, object?> { ["PatientId"] = "p1" },
            ReferenceLookups: [new MappedReferenceLookup("EncounterId", "Encounter", "EncounterId", "e1")]);
        var encounterRecord = new MappedDestinationRecord(
            Guid.NewGuid(), "Encounter", "Encounter", "e1",
            new Dictionary<string, object?> { ["EncounterId"] = "e1" },
            ReferenceLookups: [new MappedReferenceLookup("PatientId", "Patient", "PatientId", "p1")]);
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.Mapping,
            new MappedRecordBatch([patientRecord, encounterRecord]), WorkflowDataContract.MappedRecordBatch);

        var context = CreateContext();
        await executor.ExecuteAsync(context, node, [upstream], CancellationToken.None);

        writeOrder.Should().HaveCount(2, "both resources must still be written despite the cycle, just in a fallback order");

        capturedFailure.Should().NotBeNull("the cycle must be reported, not silently swallowed");
        capturedFailure!.ExceptionType.Should().Be("CircularReferenceFallback");
        capturedFailure.Message.Should().Contain("Patient").And.Contain("Encounter");
        // Names the actual fallback order used, so whoever reads this knows exactly what happened, not just that
        // something did.
        capturedFailure.Message.Should().Contain(writeOrder[0]).And.Contain(writeOrder[1]);

        capturedContext.Should().NotBeNull();
        capturedContext!.Severity.Should().Be("Informational", "a cycle fallback is a handled condition, not an incident");
        capturedContext.CorrelationId.Should().Be(context.CorrelationId);
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
