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
    }
}
