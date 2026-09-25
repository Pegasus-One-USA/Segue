using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Covers the fix for a real production bug: MappingNodeExecutor used to emit SeparateDestination child-table
/// rows as separate flat MappedDestinationRecords carrying their OWN DestinationObject (e.g. "PatientName") —
/// but MappedSqlServerDestinationWriter.WriteAsync resolves a single target table for the whole batch, so those
/// child rows were written straight into the PARENT's table, failing with "Invalid column name" for every
/// child-only column. Child rows must instead travel as MappedChildTableRecords attached to the parent row.
/// Also covers the multi-resource mapping fix: a destination selecting more than one resource (Patient +
/// Observation + Condition) previously had only one MappingProfile wired to its single MappingNode — every OTHER
/// resource type silently got that one profile's fields applied to it too (only "Id" ever matched, since it's
/// the one property every FHIR resource happens to share; everything else came back empty). MappingNodeExecutor
/// must resolve one profile per resource type and apply only the matching one, skipping any resource type with
/// no configured mapping.
/// </summary>
public sealed class MappingNodeExecutorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Child_table_rows_are_attached_to_the_parent_record_not_emitted_flat()
    {
        var fields = new[]
        {
            new MappingFieldDto("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "Patient", ArrayPolicy: ArrayPolicy.Scalar),
            new MappingFieldDto("family", "$.name[*].family", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName",
                ArrayPolicy: ArrayPolicy.SeparateDestination, ParentTable: "Patient", ParentKeyColumn: "Id", ForeignKeyColumn: "PatientId"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Active"] = true },
            Errors: [],
            Rows: null,
            ChildTables: [new MappingChildTableDto("PatientName", [new Dictionary<string, object?> { ["family"] = "Lopez", ["RowIndex"] = "0" }])]));
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1, "child rows must attach to the parent record, not become their own top-level record");
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.DestinationObject.Should().Be("Patient");
        record.Values["Active"].Should().Be(true);
        record.ChildTables.Should().HaveCount(1);
        var childTable = record.ChildTables!.Single();
        childTable.TableName.Should().Be("PatientName");
        childTable.ForeignKeyColumn.Should().Be("PatientId");
        childTable.ParentKeyColumn.Should().Be("Id");
        childTable.Rows.Single()["family"].Should().Be("Lopez");
    }

    [Fact]
    public async Task Child_table_with_no_ForeignKeyColumn_metadata_is_dropped_not_written()
    {
        var fields = new[]
        {
            new MappingFieldDto("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "Patient", ArrayPolicy: ArrayPolicy.Scalar),
            new MappingFieldDto("family", "$.name[*].family", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName", ArrayPolicy: ArrayPolicy.SeparateDestination),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Active"] = true },
            Errors: [],
            Rows: null,
            ChildTables: [new MappingChildTableDto("PatientName", [new Dictionary<string, object?> { ["family"] = "Lopez" }])]));
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.ChildTables.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task A_node_dedicated_entirely_to_a_child_table_still_emits_a_record_to_carry_it()
    {
        // No root-level field at all — mapped.Values would be empty — but the child table must still be
        // written somewhere, since the writer needs a parent row write to capture the FK value off of.
        var fields = new[]
        {
            new MappingFieldDto("family", "$.name[*].family", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName",
                ArrayPolicy: ArrayPolicy.SeparateDestination, ParentTable: "Patient", ParentKeyColumn: "Id", ForeignKeyColumn: "PatientId"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?>(),
            Errors: [],
            Rows: null,
            ChildTables: [new MappingChildTableDto("PatientName", [new Dictionary<string, object?> { ["family"] = "Lopez" }])]));
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1);
        ((MappedDestinationRecord)batch.Records.Single()).ChildTables.Should().HaveCount(1);
    }

    /// <summary>
    /// Regression test for a real production bug: resolving a resource type's profile by the (resourceType,
    /// sourceConnectionId, destinationId) triple let a workflow silently pick up (and overwrite) a DIFFERENT
    /// workflow's profile whenever both shared the same source connection + destination + resource type. The
    /// fix: resolve strictly by this node's own mappingProfileId(s) — an id it saved itself — even when
    /// sourceConnectionId/destinationId are also present on the node; the natural-key search must never run.
    /// </summary>
    [Fact]
    public async Task Resolves_the_profile_by_this_nodes_own_mappingProfileId_even_when_sourceConnectionId_and_destinationId_are_also_present()
    {
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var realProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [new MappingField("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(realProfile);

        var executor = new MappingNodeExecutor(
            new FakeJsonMappingEngine(new MappingTestResultDto(new Dictionary<string, object?> { ["Active"] = true }, [])),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
            ["mappingProfileId"] = profileId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["Active"].Should().Be(true);
        repository.Verify(
            r => r.FindMappingProfileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "resolution must never search by the (resourceType, sourceConnectionId, destinationId) triple — more than one workflow can share it");
    }

    [Fact]
    public async Task Falls_back_to_mappingProfileId_when_the_node_has_no_natural_key_fields()
    {
        // An older node saved before sourceConnectionId/destinationId were stamped onto it.
        var profileId = Guid.NewGuid();
        var realProfile = new MappingProfile(
            "Patient", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient",
            [new MappingField("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(realProfile);

        var executor = new MappingNodeExecutor(
            new FakeJsonMappingEngine(new MappingTestResultDto(new Dictionary<string, object?> { ["Active"] = true }, [])),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["mappingProfileId"] = profileId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        ((MappedDestinationRecord)batch.Records.Single()).Values["Active"].Should().Be(true);
        repository.Verify(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression test for a real production incident: a single Field Mapping node fed by an EpicSource node
    /// configured for BOTH Patient and Observation scopes received a mixed batch, and the executor applied its
    /// one resolved Patient profile to every envelope — Observation JSON got walked against Patient's fields,
    /// matching only the shared root "id", and silently produced a garbage "Patient" row (Observation's id as
    /// PatientId, every real Patient column null). Each resource type must resolve and use its OWN profile.
    /// </summary>
    [Fact]
    public async Task Different_resource_types_in_the_same_batch_are_each_mapped_through_their_own_profile()
    {
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var patientProfileId = Guid.NewGuid();
        var observationProfileId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [
                new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField"),
                new MappingField("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null, Format: "directField"),
            ]);
        var observationProfile = new MappingProfile(
            "Observation", "Observation", sourceConnectionId, destinationId, "Observation",
            [
                new MappingField("ObservationId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField"),
                new MappingField("Status", "$.status", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField"),
            ]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(patientProfileId, It.IsAny<CancellationToken>())).ReturnsAsync(patientProfile);
        repository.Setup(r => r.GetMappingProfileAsync(observationProfileId, It.IsAny<CancellationToken>())).ReturnsAsync(observationProfile);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
            ["mappingProfileIds"] = new Dictionary<string, string>
            {
                ["Patient"] = patientProfileId.ToString(),
                ["Observation"] = observationProfileId.ToString(),
            },
        });
        var upstream = UpstreamWith(
            new ResourceEnvelope("Patient", "p1", """{"id":"p1","active":true}"""),
            new ResourceEnvelope("Observation", "o1", """{"id":"o1","status":"final"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(2);
        var records = batch.Records.Cast<MappedDestinationRecord>().ToList();

        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.DestinationObject.Should().Be("Patient");
        patientRecord.Values["PatientId"].Should().Be("p1");
        patientRecord.Values["Active"].Should().Be(true);
        patientRecord.Values.Should().NotContainKey("Status", "Observation's field must never be applied to a Patient record");

        var observationRecord = records.Single(r => r.ResourceType == "Observation");
        observationRecord.DestinationObject.Should().Be("Observation");
        observationRecord.Values["ObservationId"].Should().Be("o1");
        observationRecord.Values["Status"].Should().Be("final");
        observationRecord.Values.Should().NotContainKey("Active", "Patient's field must never be applied to an Observation record");
    }

    [Fact]
    public async Task A_resource_type_with_no_resolvable_profile_is_skipped_not_mapped_through_the_nodes_own_profile()
    {
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var patientProfileId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(patientProfileId, It.IsAny<CancellationToken>())).ReturnsAsync(patientProfile);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        // Node's own configured resourceType is "Patient" — the Observation envelope has no mappingProfileIds
        // entry at all, and must NOT fall back to Patient's profile.
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
            ["mappingProfileIds"] = new Dictionary<string, string> { ["Patient"] = patientProfileId.ToString() },
        });
        var upstream = UpstreamWith(
            new ResourceEnvelope("Patient", "p1", """{"id":"p1"}"""),
            new ResourceEnvelope("Observation", "o1", """{"id":"o1","status":"final"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1, "the Observation envelope has no resolvable profile and must be skipped, not mapped through Patient's fields");
        ((MappedDestinationRecord)batch.Records.Single()).ResourceType.Should().Be("Patient");
    }

    /// <summary>
    /// Regression test for the Medplum end-to-end bug: a whole-resource FHIR destination (Medplum / FHIR repository)
    /// persists the source resource itself (SourceJson), so it legitimately has NO field mappings. The executor's
    /// "emit only when at least one Value mapped" gate previously dropped every resource, landing zero records
    /// (ExportHistory NoData). For these destinations it must emit one carrier record per resource carrying SourceJson.
    /// </summary>
    [Fact]
    public async Task Whole_resource_fhir_destination_emits_a_record_per_resource_carrying_SourceJson_with_no_field_mappings()
    {
        var destinationId = Guid.NewGuid();
        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationConfiguration(
                "Medplum Production", DestinationType.Medplum, new SecretReference("kv", "secret"),
                "https://api.medplum.com/fhir/R4", null));

        // Engine returns no mapped Values — mirrors a destination with zero column mappings.
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?>(), Errors: [], Rows: null, ChildTables: null));
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        const string patientJson = """{"resourceType":"Patient","id":"p1","identifier":[{"system":"http://hapi","value":"p1"}]}""";
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", patientJson));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1, "a whole-resource FHIR destination must still emit a carrier record even with no field mappings");
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.ResourceType.Should().Be("Patient");
        record.SourceJson.Should().Be(patientJson, "the Medplum writer persists SourceJson, so it must be carried through");
    }

    [Fact]
    public async Task An_unrequested_resource_type_is_dropped_silently_not_reported_as_skipped()
    {
        // Reproduces a real production symptom: a Group $export scoped to a lone "Patient" resource type omits
        // the _type parameter entirely to dodge an Epic bug, so Epic hands back every resource type it supports —
        // dozens the workflow never configured a mapping for. Only "Patient" appears in mappingProfileIds here,
        // so Observation must be dropped (nothing tells this node how to map it) WITHOUT being reported in
        // skippedResourceTypes — it was never part of this workflow, so flagging it reads as a false "partial
        // success" even though nothing is actually broken.
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var patientProfileId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(patientProfileId, It.IsAny<CancellationToken>())).ReturnsAsync(patientProfile);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
            ["mappingProfileIds"] = new Dictionary<string, string> { ["Patient"] = patientProfileId.ToString() },
        });
        var upstream = UpstreamWith(
            new ResourceEnvelope("Patient", "p1", """{"id":"p1"}"""),
            new ResourceEnvelope("Observation", "o1", """{"id":"o1","status":"final"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        output.Metadata.Should().NotBeNull();
        output.Metadata!["skippedResourceTypes"].Should().BeNull(
            "Observation was never configured on this node — it's incidental over-fetch, not a missing mapping");
    }

    [Fact]
    public async Task A_configured_but_deleted_profile_is_still_reported_as_skipped()
    {
        // The opposite of the case above: this resource type DOES have a mappingProfileIds entry, but the
        // profile it points at no longer resolves (deleted). That's a real misconfiguration the workflow owner
        // should hear about, so it must still land in skippedResourceTypes.
        var observationProfileId = Guid.NewGuid();
        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(observationProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MappingProfile?)null);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        // Node's own default resourceType is "Patient" — Observation is a DIFFERENT, explicitly configured
        // entry (not the node's default), isolating the "has a mappingProfileIds entry but it's unresolvable"
        // branch from the "matches the node's own configuredResourceType" branch.
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["mappingProfileIds"] = new Dictionary<string, string> { ["Observation"] = observationProfileId.ToString() },
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Observation", "o1", """{"id":"o1"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        output.Metadata!["skippedResourceTypes"].Should().BeAssignableTo<string[]>()
            .Which.Should().Contain("Observation");
    }

    [Fact]
    public async Task Maps_each_resource_type_using_only_its_own_configured_mappingProfileIds_entry()
    {
        var patientProfileId = Guid.NewGuid();
        var conditionProfileId = Guid.NewGuid();

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(patientProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LegacyProfile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));
        repository.Setup(r => r.GetMappingProfileAsync(conditionProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LegacyProfile("Condition", "conditions.csv", ("ConditionId", "$.id"), ("Code", "$.code.coding[*].display")));

        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            mappingProfileIds = new Dictionary<string, string>
            {
                ["Patient"] = patientProfileId.ToString(),
                ["Condition"] = conditionProfileId.ToString(),
            },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: repository.Object);
        var context = CreateContext();

        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "p1", "{}"),
                new ResourceEnvelope("Condition", "c1", "{}"),
                new ResourceEnvelope("Observation", "o1", "{}")), // no profile configured for this one
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);

        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().HaveCount(2); // Observation dropped — no mapping configured for it on this node

        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.DestinationObject.Should().Be("patients.csv");
        patientRecord.Values.Keys.Should().BeEquivalentTo(["Id", "NameFamily"]);

        var conditionRecord = records.Single(r => r.ResourceType == "Condition");
        conditionRecord.DestinationObject.Should().Be("conditions.csv");
        conditionRecord.Values.Keys.Should().BeEquivalentTo(["ConditionId", "Code"]);
    }

    [Fact]
    public async Task Legacy_single_mappingProfileId_still_resolves_but_no_longer_leaks_onto_other_resource_types()
    {
        var profileId = Guid.NewGuid();
        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LegacyProfile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));

        var node = BuildLegacyNode($$"""{"mappingProfileId":"{{profileId}}"}""");
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: repository.Object);
        var context = CreateContext();

        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "p1", "{}"),
                new ResourceEnvelope("Observation", "o1", "{}")),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        // Only the Patient record survives — pre-fix, the Observation record would have been mapped with Patient's
        // own fields too (NameFamily etc. empty, since Observation JSON has no such path).
        records.Should().ContainSingle();
        records[0].ResourceType.Should().Be("Patient");
    }

    [Fact]
    public async Task No_repository_composed_falls_back_to_inline_config_for_a_single_resource_type()
    {
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            resourceType = "Patient",
            destinationObject = "patients.csv",
            fields = new[] { new MappingFieldDto("Id", "$.id", MappingValueType.String, false, null, null) },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = CreateContext();

        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "p1", "{}"),
                new ResourceEnvelope("Observation", "o1", "{}")),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].ResourceType.Should().Be("Patient");
        records[0].DestinationObject.Should().Be("patients.csv");
    }

    /// <summary>
    /// End-to-end proof that the transform-rule engine is actually wired into this executor: when a
    /// destination and the rule dependencies are supplied, a resolved rule is applied to the mapped value
    /// before it lands in the record — the whole point of this wiring (see FHIRBridge.Application.Services
    /// .Transforms). Uses a REAL TransformNodeRegistry/StringNormalizationNode (not a fake) so this proves the
    /// actual node execution path, not just that a mock was called.
    /// </summary>
    [Fact]
    public async Task Applies_a_resolved_transform_rule_to_the_mapped_value_before_it_is_written()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name.family", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["FamilyName"] = "roe" }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["case"] = "upper" }),
            resourceType: "Patient", destinationField: "FamilyName");
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "FamilyName", It.IsAny<Guid?>(), null, "Patient.name.family",
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["FamilyName"].Should().Be("ROE", "the resolved StringNormalization rule (case=upper) must run before the value is written");
    }

    /// <summary>
    /// A rule on a field inside a repeating element must apply to EVERY instance of it, not only to whichever
    /// one the parent row happened to carry. A field mapped with ArrayPolicy.SeparateDestination writes its
    /// values into the array's own table instead of onto the parent row, and only the parent row was ever put
    /// through the rule chain — so the same rule on the same field ran or did not run purely according to which
    /// table the mapping targeted, and every row in dbo.PatientName kept the raw source value.
    /// </summary>
    [Fact]
    public async Task Applies_a_resolved_transform_rule_to_every_child_table_row()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "Patient", ArrayPolicy: ArrayPolicy.Scalar),
            new MappingFieldDto("Text", "$.name[*].text", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName",
                ArrayPolicy: ArrayPolicy.SeparateDestination, ParentTable: "Patient", ParentKeyColumn: "Id",
                ForeignKeyColumn: "PatientId"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Active"] = true },
            Errors: [],
            Rows: null,
            ChildTables:
            [
                new MappingChildTableDto("PatientName",
                [
                    new Dictionary<string, object?> { ["Text"] = "jane roe", ["RowIndex"] = "0" },
                    new Dictionary<string, object?> { ["Text"] = "j. roe", ["RowIndex"] = "1" },
                ]),
            ]));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["case"] = "upper" }),
            resourceType: "Patient", destinationField: "Text");
        var resolver = new Mock<IEffectiveRuleResolver>();
        // Every other field (Active) still asks the resolver — without a catch-all it hands back a null task.
        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "Text", It.IsAny<Guid?>(), null, "Patient.name.text",
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        var childRows = record.ChildTables!.Single().Rows;
        childRows.Select(r => r["Text"])
            .Should().Equal(["JANE ROE", "J. ROE"], "every instance of the repeating field goes through the same chain");
    }

    /// <summary>
    /// The child rows' rules must resolve against the child field's OWN source path. The executor's
    /// parent-row source map is keyed by target field across every field of the resource, so a child column and
    /// a parent column sharing a name collide there — reusing it would look the child's rule up under the
    /// parent field's path and silently apply the wrong chain (or none).
    /// </summary>
    [Fact]
    public async Task A_child_column_sharing_a_parent_columns_name_resolves_its_own_source_field()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            // Same column name on both tables, different source paths.
            new MappingFieldDto("Text", "$.maritalStatus.text", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient",
                ArrayPolicy: ArrayPolicy.Scalar),
            new MappingFieldDto("Text", "$.name[*].text", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName",
                ArrayPolicy: ArrayPolicy.SeparateDestination, ParentTable: "Patient", ParentKeyColumn: "Id",
                ForeignKeyColumn: "PatientId"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Text"] = "married" },
            Errors: [],
            Rows: null,
            ChildTables:
            [
                new MappingChildTableDto("PatientName",
                    [new Dictionary<string, object?> { ["Text"] = "jane roe", ["RowIndex"] = "0" }]),
            ]));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        // Only the CHILD field's path has a rule; the parent's identically-named column has none.
        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["case"] = "upper" }),
            resourceType: "Patient", destinationField: "Text");
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "Text", It.IsAny<Guid?>(), null, "Patient.name.text",
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var record = (MappedDestinationRecord)((MappedRecordBatch)output.Payload!).Records.Single();
        record.ChildTables!.Single().Rows.Single()["Text"].Should().Be("JANE ROE");
        record.Values["Text"].Should().Be("married", "the parent column of the same name has no rule of its own");
    }

    /// <summary>
    /// "All records" on a whole-node mapping with a transformation: the rule runs once per repeat and the
    /// column receives one delimited string of the rendered names, not a JSON array of them. A column holding
    /// `["Warren McGinnis","Warren McGinnis"]` reads as an encoding artefact where the delimited form reads as
    /// the list it is.
    /// </summary>
    [Fact]
    public async Task All_records_writes_every_transformed_name_as_one_delimited_string()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("NameConcat", "$.name", MappingValueType.Json, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "Patient",
                ArrayPolicy: ArrayPolicy.StoreJson),
        };

        // What ArrayPolicy.StoreJson writes for a repeating element: the node's raw JSON text.
        const string nameArray =
            """[{"use":"official","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]},{"use":"usual","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]}]""";
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["NameConcat"] = nameArray }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.HumanNameParsing,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["pattern"] = "FirstLast" }),
            resourceType: "Patient", destinationField: "NameConcat",
            arrayMode: TransformArrayMode.PerItem);
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new HumanNameParsingNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var record = (MappedDestinationRecord)((MappedRecordBatch)output.Payload!).Records.Single();
        record.Values["NameConcat"].Should().Be("Warren McGinnis, Warren McGinnis");
    }

    /// <summary>
    /// A PerItem run over a one-element array is a single value, so it still reaches the ExpectedValueType
    /// coercion a strictly-typed column needs — joining it into a string skipped that, and PostgreSQL/MySQL
    /// parameter binding refuses a text value for an integer/date column.
    /// </summary>
    [Fact]
    public async Task A_single_element_PerItem_result_is_still_coerced_to_the_expected_type()
    {
        var destination = new DestinationConfiguration(
            "Test PG", DestinationType.PostgreSql, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("Score", "$.extension", MappingValueType.Json, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "Patient",
                ArrayPolicy: ArrayPolicy.StoreJson),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Score"] = """[" 42 "]""" }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["trim"] = "true" }),
            resourceType: "Patient", destinationField: "Score",
            arrayMode: TransformArrayMode.PerItem, expectedValueType: MappingValueType.Integer);
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var record = (MappedDestinationRecord)((MappedRecordBatch)output.Payload!).Records.Single();
        record.Values["Score"].Should().Be(42L);
    }

    /// <summary>
    /// RawArrayValues spans every instance of the repeating element — it was built for the parent row. A child
    /// row already IS one instance, so a chain led by ConcatenationTemplating must see that row's own value,
    /// not the same cross-instance list substituted into every row.
    /// </summary>
    [Fact]
    public async Task Child_rows_do_not_receive_the_parents_cross_instance_raw_array_values()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("Given", "$.name[*].given", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName",
                ArrayPolicy: ArrayPolicy.SeparateDestination, ParentTable: "Patient", ParentKeyColumn: "Id",
                ForeignKeyColumn: "PatientId"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?>(),
            Errors: [],
            Rows: null,
            ChildTables:
            [
                new MappingChildTableDto("PatientName",
                [
                    new Dictionary<string, object?> { ["Given"] = "Camila", ["RowIndex"] = "0" },
                    new Dictionary<string, object?> { ["Given"] = "Cami", ["RowIndex"] = "1" },
                ]),
            ],
            RawArrayValues: new Dictionary<string, IReadOnlyList<object?>> { ["Given"] = ["Camila", "Maria", "Cami"] }));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.ConcatenationTemplating,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["separator"] = " " }),
            resourceType: "Patient", destinationField: "Given");
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new ConcatenationTemplatingNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var record = (MappedDestinationRecord)((MappedRecordBatch)output.Payload!).Records.Single();
        record.ChildTables!.Single().Rows.Select(r => r["Given"]).Should().Equal(["Camila", "Cami"]);
    }

    /// <summary>
    /// End-to-end proof that the real destination type reaches the node automatically — a Unit Conversion
    /// rule writing into a SQL destination gets just the plain number, never the full FHIR Quantity object,
    /// even though nothing in the rule's own saved config says "flatten this." The executor infers it from
    /// the workflow's actual destination.
    /// </summary>
    [Fact]
    public async Task Unit_conversion_writes_a_plain_number_not_a_quantity_object_for_a_sql_destination()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("Value", "$.valueQuantity.value", MappingValueType.Decimal, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Observation", DestinationObject: "Observation"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Value"] = 38.9m }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Global, TransformNodeType.UnitConversion,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["sourceUnit"] = "Cel", ["targetUnit"] = "[degF]", ["precision"] = "1" }));
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Observation", "Value", It.IsAny<Guid?>(), null, "Observation.valueQuantity.value",
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new UnitConversionNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Observation", "Observation", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Observation", "o1", """{"resourceType":"Observation"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["Value"].Should().Be(102.0m);
    }

    /// <summary>
    /// "TransformationRules:Hidden" (Settings &gt; System Settings &gt; General, default true) gates the
    /// feature's UI surfaces ONLY — the destination wizard's Rules button and the Settings &gt; Transformation
    /// Rules screen. It must never gate rule application at runtime: hiding the configuration screen cannot
    /// silently change what a workflow writes, or an admin tidying up the UI would corrupt output for every
    /// pipeline with rules already configured.
    ///
    /// This test previously asserted the opposite — that the flag suppressed execution — which was the
    /// behaviour commit a0b1dad4 ("Fix transformation rules silently not applying during workflow execution")
    /// removed as bug #1 of three. The production contract is stated on TransformationRulesFeatureFlag itself;
    /// this test now pins that contract so the gate cannot be reintroduced.
    /// </summary>
    [Fact]
    public async Task Applies_rules_even_when_the_feature_flag_reads_hidden()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name.family", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["FamilyName"] = "roe" }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["case"] = "upper" }),
            resourceType: "Patient", destinationField: "FamilyName");
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "FamilyName", It.IsAny<Guid?>(), null, "Patient.name.family",
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["FamilyName"].Should().Be(
            "ROE",
            "the flag hides the rules UI only — a resolved rule must still be applied during execution");
    }

    [Fact]
    public async Task Is_a_no_op_when_no_rule_dependencies_are_supplied_existing_behavior_unchanged()
    {
        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name.family", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["FamilyName"] = "roe" }, Errors: []));
        // No configurationRepository, no ruleResolver, no transformNodeRegistry — the optional-dependency path.
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        ((MappedDestinationRecord)batch.Records.Single()).Values["FamilyName"].Should().Be("roe");
    }

    // FHIR-repository passthrough (e.g. Aidbox): when this node's config carries destinationTransformId=dest-fhir
    // (stamped by WorkflowGraphMapperService.syntheticMappingRequest, which copies the destination node's own
    // fields onto the synthetic Field Mapping node) and dest_fhirMapMode isn't "customize", every resource must be
    // emitted unchanged — no configured mapping profile required at all. Before this fix, a resource type with no
    // configured field-mapping profile was silently dropped (see the three tests above), which meant a passthrough
    // FHIR destination always received zero records and wrote nothing, while the workflow run still reported
    // "Succeeded" (confirmed against a real Aidbox instance).
    [Fact]
    public async Task FhirRepository_passthrough_emits_every_resource_unchanged_with_no_mapping_profile_configured()
    {
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005","name":[{"family":"Iyer","given":["Divya"]}]}""";
        var observationJson = """{"resourceType":"Observation","id":"obs-1"}""";
        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "1005", patientJson),
                new ResourceEnvelope("Observation", "obs-1", observationJson)),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().HaveCount(2);
        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.SourceJson.Should().Be(patientJson);
        patientRecord.Values.Should().BeEmpty();
        patientRecord.SourceResourceId.Should().Be("1005");

        var observationRecord = records.Single(r => r.ResourceType == "Observation");
        observationRecord.SourceJson.Should().Be(observationJson);
        observationRecord.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task FhirRepository_explicit_passthrough_mode_behaves_identically_to_absent_dest_fhirMapMode()
    {
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "passthrough",
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005"}""";
        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "1005", patientJson)) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].SourceJson.Should().Be(patientJson);
    }

    [Fact]
    public async Task FhirRepository_customize_mode_with_no_rules_configured_emits_every_resource_unchanged()
    {
        // No dest_fhirCustomRules at all (or none scoped to this resource type) — customize mode still takes its
        // own passthrough-shaped branch (not the field-mapping engine below), it just has zero rules to apply.
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "customize",
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005"}""";
        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "1005", patientJson)) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].SourceJson.Should().Be(patientJson);
        output.Metadata!["customize"].Should().Be(true);
    }

    [Fact]
    public async Task FhirRepository_customize_mode_applies_rules_scoped_to_the_matching_resource_type_only()
    {
        var rules = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Patient"] = new[]
            {
                new { field = "active", transform = "booleanConversion", @params = new { trueValues = "Y" } },
            },
            // Scoped to a resource type not present in this batch — must have no effect on Observation below.
            ["Observation"] = new[]
            {
                new { field = "status", transform = "typeCast", @params = new { targetType = "integer" } },
            },
        });
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "customize",
            dest_fhirCustomRules = rules,
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005","active":"Y"}""";
        var observationJson = """{"resourceType":"Observation","id":"obs-1","status":"final"}""";
        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "1005", patientJson),
                new ResourceEnvelope("Observation", "obs-1", observationJson)),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.SourceJson.Should().Contain("\"active\":true");

        // Observation's rule never applies here (it's scoped to a resource type absent from this batch) —
        // unchanged, proving rules are matched by the resource's own type, not applied globally.
        var observationRecord = records.Single(r => r.ResourceType == "Observation");
        observationRecord.SourceJson.Should().Be(observationJson);
    }

    [Fact]
    public async Task FhirRepository_customize_mode_isolates_a_failing_rule_reports_it_but_still_emits_the_resource()
    {
        var rules = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Patient"] = new[]
            {
                new { field = "birthDate", transform = "dateFormat", @params = new { } },
            },
        });
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "customize",
            dest_fhirCustomRules = rules,
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        // birthDate is not a parseable date — the rule must fail in isolation, not throw out of ExecuteAsync.
        var patientJson = """{"resourceType":"Patient","id":"1005","birthDate":"not-a-date"}""";
        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "1005", patientJson)) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].SourceJson.Should().Be(patientJson); // unmodified — the failed rule left it as-is
        var ruleErrors = output.Metadata!["ruleErrors"].Should().BeAssignableTo<List<string>>().Subject;
        ruleErrors.Should().ContainSingle(e => e.Contains("Patient.birthDate") && e.Contains("dateFormat"));
    }

    [Fact]
    public async Task Non_fhir_destination_is_unaffected_by_the_passthrough_shortcut()
    {
        // Regression guard: a destinationTransformId that isn't "dest-fhir" (e.g. a SQL Server destination node's
        // own fields, also spread onto the synthetic node) must never take the passthrough shortcut, even though
        // this node's config now carries that key too.
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-sqlserver",
            resourceType = "Patient",
            destinationObject = "patients",
            fields = new[] { new MappingFieldDto("Id", "$.id", MappingValueType.String, false, null, null) },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "p1", "{}")) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].Values.Keys.Should().BeEquivalentTo(["Id"]); // real field mapping ran, not passthrough
    }

    private static WorkflowNode CreateNode(
        string resourceType, string destinationObject, IReadOnlyCollection<MappingFieldDto> fields,
        IReadOnlyDictionary<string, object>? extraConfig = null)
    {
        var config = new Dictionary<string, object>
        {
            ["resourceType"] = resourceType,
            ["destinationObject"] = destinationObject,
            ["fields"] = fields,
        };
        foreach (var (key, value) in extraConfig ?? new Dictionary<string, object>())
        {
            config[key] = value;
        }

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "mapping-node-test", 1);
        return workflow.AddNode(
            WorkflowNodeTypes.Mapping,
            WorkflowNodeCategory.Transform,
            60,
            configurationJson: JsonSerializer.Serialize(config, JsonOptions));
    }

    private static WorkflowNode BuildLegacyNode(string configurationJson)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "mapping-test", 1);
        return workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 50, configurationJson: configurationJson);
    }

    private static WorkflowNodeOutput UpstreamWith(params ResourceEnvelope[] resources) =>
        new(Guid.NewGuid(), WorkflowNodeTypes.EpicSource, new ResourceBatch(resources), WorkflowDataContract.ResourceBatch);

    private static WorkflowNodeOutput SourceOutput(params ResourceEnvelope[] resources) =>
        new(Guid.NewGuid(), WorkflowNodeTypes.EpicSource, new ResourceBatch(resources), WorkflowDataContract.ResourceBatch);

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test-correlation");

    // Echoes the profile's own fields back as Values (TargetField -> its JsonPath) regardless of source JSON — lets
    // assertions prove exactly which fields were applied to which resource, without a real JSONPath engine.
    private static Mock<IJsonMappingEngine> EchoFieldsEngine()
    {
        var engine = new Mock<IJsonMappingEngine>();
        engine
            .Setup(e => e.Map(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<MappingFieldDto>>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((string _, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? _) =>
                new MappingTestResultDto(
                    fields.ToDictionary(f => f.TargetField, object? (f) => f.JsonPath),
                    []));
        return engine;
    }

    private static MappingProfile LegacyProfile(string resourceType, string destinationObject, params (string Target, string JsonPath)[] fields) =>
        new(
            $"{resourceType} mapping",
            resourceType,
            Guid.NewGuid(),
            Guid.NewGuid(),
            destinationObject,
            fields.Select(f => new MappingField(f.Target, f.JsonPath, MappingValueType.String, false, null, null)));

    private sealed class FakeJsonMappingEngine : IJsonMappingEngine
    {
        private readonly MappingTestResultDto _result;
        public FakeJsonMappingEngine(MappingTestResultDto result) => _result = result;
        public MappingTestResultDto Map(
            string sourceJson, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? systemValues = null) => _result;
    }

    /// <summary>A minimal real mapper (unlike <see cref="FakeJsonMappingEngine"/>'s fixed canned result) that
    /// resolves each field's simple root-level "$.property" JsonPath against the actual source JSON — needed to
    /// prove that different resource types in one batch really do get mapped through their OWN distinct fields,
    /// not a fixed stand-in result that couldn't tell the two apart.</summary>
    private sealed class RoutingFakeJsonMappingEngine : IJsonMappingEngine
    {
        public MappingTestResultDto Map(
            string sourceJson, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? systemValues = null)
        {
            using var document = JsonDocument.Parse(sourceJson);
            var values = new Dictionary<string, object?>();
            foreach (var field in fields)
            {
                var propertyName = field.JsonPath.TrimStart('$', '.');
                if (!document.RootElement.TryGetProperty(propertyName, out var element))
                {
                    continue;
                }

                values[field.TargetField] = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
                    _ => null
                };
            }

            return new MappingTestResultDto(values, []);
        }
    }

    /// <summary>
    /// Regression test for a rule-authority bug: the final relational-type coercion must be driven only by the
    /// last rule that actually SUCCEEDED, never by one the chain merely reached and then failed. Chain: a
    /// NumberCast rule (declares ExpectedValueType=Boolean, which is nonsensical for it — a red herring meant
    /// to look plausible if wrongly credited) fails on non-numeric input and PassesThrough; a StringNormalization
    /// rule with NO declared type then actually produces the final value. Before this fix, the failed
    /// NumberCast rule's Boolean type still got credited, so the final string "yes" (a real value, not a literal
    /// boolean spelling coincidence) was silently miscoerced to the CLR `true` before being written.
    /// </summary>
    [Fact]
    public async Task A_rule_that_fails_never_dictates_the_final_coercion_type()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name.family", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["FamilyName"] = "YES" }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var failingTypedRule = new TransformationRule(
            TransformScope.Field, TransformNodeType.NumberCast, JsonSerializer.Serialize(new Dictionary<string, string>()),
            resourceType: "Patient", destinationField: "FamilyName",
            errorPolicy: TransformErrorPolicy.PassThrough, expectedValueType: MappingValueType.Boolean);
        var succeedingUntypedRule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["case"] = "lower" }),
            resourceType: "Patient", destinationField: "FamilyName");

        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "FamilyName", It.IsAny<Guid?>(), null, "Patient.name.family",
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[failingTypedRule, succeedingUntypedRule]);

        var registry = new TransformNodeRegistry([new NumberCastNode(), new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["FamilyName"].Should().Be("yes",
            "the failed NumberCast rule's Boolean type must not be applied to a value it never produced — " +
            "only the StringNormalization rule that actually ran wrote this value, and it declares no type");
    }
}
