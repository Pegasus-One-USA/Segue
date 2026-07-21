using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
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

// Covers the multi-resource mapping fix: a destination selecting more than one resource (Patient + Observation +
// Condition) previously had only one MappingProfile wired to its single MappingNode — every OTHER resource type
// silently got that one profile's fields applied to it too (only "Id" ever matched, since it's the one property
// every FHIR resource happens to share; everything else came back empty). MappingNodeExecutor must now resolve one
// profile per resource type and apply only the matching one, skipping any resource type with no configured mapping.
public sealed class MappingNodeExecutorTests
{
    private static WorkflowNode BuildNode(string configurationJson)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "mapping-test", 1);
        return workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 50, configurationJson: configurationJson);
    }

    private static WorkflowNodeOutput SourceOutput(params ResourceEnvelope[] resources) =>
        new(Guid.NewGuid(), WorkflowNodeTypes.EpicSource, new ResourceBatch(resources), WorkflowDataContract.ResourceBatch);

    // Echoes the profile's own fields back as Values (TargetField -> its JsonPath) regardless of source JSON — lets
    // assertions prove exactly which fields were applied to which resource, without a real JSONPath engine.
    private static Mock<IJsonMappingEngine> EchoFieldsEngine()
    {
        var engine = new Mock<IJsonMappingEngine>();
        engine
            .Setup(e => e.Map(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<MappingFieldDto>>()))
            .Returns((string _, IReadOnlyCollection<MappingFieldDto> fields) =>
                new MappingTestResultDto(
                    fields.ToDictionary(f => f.TargetField, object? (f) => f.JsonPath),
                    []));
        return engine;
    }

    private static MappingProfile Profile(string resourceType, string destinationObject, params (string Target, string JsonPath)[] fields) =>
        new(
            $"{resourceType} mapping",
            resourceType,
            Guid.NewGuid(),
            Guid.NewGuid(),
            destinationObject,
            fields.Select(f => new MappingField(f.Target, f.JsonPath, MappingValueType.String, false, null, null)));

    [Fact]
    public async Task Maps_each_resource_type_using_only_its_own_configured_profile()
    {
        var patientProfileId = Guid.NewGuid();
        var conditionProfileId = Guid.NewGuid();

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(patientProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));
        repository.Setup(r => r.GetMappingProfileAsync(conditionProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile("Condition", "conditions.csv", ("ConditionId", "$.id"), ("Code", "$.code.coding[*].display")));

        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            mappingProfileIds = new Dictionary<string, string>
            {
                ["Patient"] = patientProfileId.ToString(),
                ["Condition"] = conditionProfileId.ToString(),
            },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, repository.Object);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

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
            .ReturnsAsync(Profile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));

        var node = BuildNode($$"""{"mappingProfileId":"{{profileId}}"}""");
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, repository.Object);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

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
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            resourceType = "Patient",
            destinationObject = "patients.csv",
            fields = new[] { new MappingFieldDto("Id", "$.id", MappingValueType.String, false, null, null) },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

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
}
