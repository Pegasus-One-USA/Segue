using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// P3: field-level lineage — answers "why does this destination column hold this value" by capturing which source
// FHIR path (and transformation) produced each mapped field, per resource. Opt-in (FieldLineageOptions.Enabled),
// since the row volume is O(fields × resources), not O(resources) like the resource-level trail.
public sealed class MappingNodeExecutorFieldLineageTests
{
    private static readonly MappingFieldDto[] Fields =
    [
        new MappingFieldDto("FullName", "Patient.name[0].family", MappingValueType.String, IsRequired: true, DefaultValue: null, Format: null),
        new MappingFieldDto(
            "ObservationName", "Observation.code.coding[0].display", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null,
            TerminologySystemJsonPath: "Observation.code.coding[0].system",
            TerminologyCodeJsonPath: "Observation.code.coding[0].code"),
    ];

    [Fact]
    public async Task Enabled_records_one_field_lineage_entry_per_mapped_field_with_correct_transformation_type()
    {
        var (executor, recorded) = BuildExecutor(fieldLineageEnabled: true);
        var node = BuildNode();
        var inputs = SourceInputs();

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, inputs, CancellationToken.None);

        recorded.Should().HaveCount(2);
        recorded.Should().ContainSingle(r =>
            r.SourceFieldPath == "Patient.name[0].family"
            && r.TransformationType == "DirectCopy"
            && r.DestinationColumn == "FullName"
            && r.ResourceType == "Observation"
            && r.SourceResourceId == "obs1");
        recorded.Should().ContainSingle(r =>
            r.SourceFieldPath == "Observation.code.coding[0].display"
            && r.TransformationType == "TerminologyTranslation"
            && r.DestinationColumn == "ObservationName");
    }

    [Fact]
    public async Task Disabled_by_default_records_nothing()
    {
        var (executor, recorded) = BuildExecutor(fieldLineageEnabled: false);
        var node = BuildNode();
        var inputs = SourceInputs();

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, inputs, CancellationToken.None);

        recorded.Should().BeEmpty();
    }

    private static (MappingNodeExecutor Executor, List<FieldLineageRecord> Recorded) BuildExecutor(bool fieldLineageEnabled)
    {
        var mappingEngine = new Mock<IJsonMappingEngine>();
        mappingEngine
            .Setup(x => x.Map(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<MappingFieldDto>>()))
            .Returns(new MappingTestResultDto(
                new Dictionary<string, object?> { ["FullName"] = "Doe", ["ObservationName"] = "Body Temperature" },
                []));

        var recorded = new List<FieldLineageRecord>();
        var fieldLineageStore = new Mock<IFieldLineageStore>();
        fieldLineageStore
            .Setup(x => x.AppendAsync(It.IsAny<FieldLineageRecord>(), It.IsAny<CancellationToken>()))
            .Callback<FieldLineageRecord, CancellationToken>((record, _) => recorded.Add(record))
            .Returns(Task.CompletedTask);

        var options = Options.Create(new FieldLineageOptions { Enabled = fieldLineageEnabled });
        var executor = new MappingNodeExecutor(mappingEngine.Object, null, fieldLineageStore.Object, options);

        return (executor, recorded);
    }

    private static WorkflowNode BuildNode()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "field-lineage-test", 1);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var configurationJson = JsonSerializer.Serialize(
            new { fields = Fields, resourceType = "Observation", destinationObject = "dbo.Observation" },
            jsonOptions);

        return workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 0, configurationJson: configurationJson);
    }

    private static WorkflowNodeOutput[] SourceInputs() =>
    [
        new WorkflowNodeOutput(
            Guid.NewGuid(),
            WorkflowNodeTypes.EpicSource,
            new ResourceBatch([new ResourceEnvelope("Observation", "obs1", "{}")]),
            WorkflowDataContract.ResourceBatch)
    ];
}
