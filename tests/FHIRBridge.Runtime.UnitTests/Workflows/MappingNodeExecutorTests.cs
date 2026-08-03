using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
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

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Covers the fix for a real production bug: MappingNodeExecutor used to emit SeparateDestination child-table
/// rows as separate flat MappedDestinationRecords carrying their OWN DestinationObject (e.g. "PatientName") —
/// but MappedSqlServerDestinationWriter.WriteAsync resolves a single target table for the whole batch, so those
/// child rows were written straight into the PARENT's table, failing with "Invalid column name" for every
/// child-only column. Child rows must instead travel as MappedChildTableRecords attached to the parent row.
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

    [Fact]
    public async Task Resolves_the_profile_by_natural_key_even_when_a_stale_mappingProfileId_is_also_present()
    {
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var realProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [new MappingField("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.FindMappingProfileAsync("Patient", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(realProfile);

        var executor = new MappingNodeExecutor(
            new FakeJsonMappingEngine(new MappingTestResultDto(new Dictionary<string, object?> { ["Active"] = true }, [])),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
            // Deliberately wrong/stale — a natural-key match must win over this rather than the executor
            // ever resolving (or falling back to) this id.
            ["mappingProfileId"] = Guid.NewGuid().ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["Active"].Should().Be(true);
        repository.Verify(
            r => r.GetMappingProfileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the natural-key lookup found a profile, so the stale mappingProfileId must never even be looked up");
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
        repository.Setup(r => r.FindMappingProfileAsync("Patient", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(patientProfile);
        repository.Setup(r => r.FindMappingProfileAsync("Observation", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(observationProfile);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
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
        var patientProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.FindMappingProfileAsync("Patient", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(patientProfile);
        repository.Setup(r => r.FindMappingProfileAsync("Observation", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MappingProfile?)null);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        // Node's own configured resourceType is "Patient" — the Observation envelope must NOT fall back to it.
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(
            new ResourceEnvelope("Patient", "p1", """{"id":"p1"}"""),
            new ResourceEnvelope("Observation", "o1", """{"id":"o1","status":"final"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1, "the Observation envelope has no resolvable profile and must be skipped, not mapped through Patient's fields");
        ((MappedDestinationRecord)batch.Records.Single()).ResourceType.Should().Be("Patient");
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

    private static WorkflowNodeOutput UpstreamWith(params ResourceEnvelope[] resources) =>
        new(Guid.NewGuid(), WorkflowNodeTypes.EpicSource, new ResourceBatch(resources), WorkflowDataContract.ResourceBatch);

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test-correlation");

    private sealed class FakeJsonMappingEngine : IJsonMappingEngine
    {
        private readonly MappingTestResultDto _result;
        public FakeJsonMappingEngine(MappingTestResultDto result) => _result = result;
        public MappingTestResultDto Map(string sourceJson, IReadOnlyCollection<MappingFieldDto> fields) => _result;

        public MappingTestResultDto Map(string sourceJson, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? systemValues = null)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>A minimal real mapper (unlike <see cref="FakeJsonMappingEngine"/>'s fixed canned result) that
    /// resolves each field's simple root-level "$.property" JsonPath against the actual source JSON — needed to
    /// prove that different resource types in one batch really do get mapped through their OWN distinct fields,
    /// not a fixed stand-in result that couldn't tell the two apart.</summary>
    private sealed class RoutingFakeJsonMappingEngine : IJsonMappingEngine
    {
        public MappingTestResultDto Map(string sourceJson, IReadOnlyCollection<MappingFieldDto> fields)
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

        public MappingTestResultDto Map(string sourceJson, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? systemValues = null)
        {
            throw new NotImplementedException();
        }
    }
}
