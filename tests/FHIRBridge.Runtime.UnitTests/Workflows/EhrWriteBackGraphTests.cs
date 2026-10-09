using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// The EHR Write-Back node takes whole FHIR resources only, straight from a source or transformation node: no Mapping
/// node (it would flatten them), and no De-identification anywhere upstream (redacted data must never reach a chart).
/// </summary>
public sealed class EhrWriteBackGraphTests
{
    private static readonly string TargetConfig = $$"""{"dest_sourceConnectionId":"{{Guid.NewGuid()}}"}""";

    [Fact]
    public void Source_straight_into_write_back_is_valid_without_a_mapping_node()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "epic-to-epic", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var writeBack = workflow.AddNode(WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, rank: 70, configurationJson: TargetConfig);
        workflow.AddEdge(source.Id, writeBack.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors));
    }

    [Fact]
    public void Transformation_in_front_of_write_back_is_valid()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "epic-transform-epic", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var transform = workflow.AddNode(WorkflowNodeTypes.FhirResourceTransform, WorkflowNodeCategory.Transform, rank: 34);
        var writeBack = workflow.AddNode(WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, rank: 70, configurationJson: TargetConfig);
        workflow.AddEdge(source.Id, transform.Id);
        workflow.AddEdge(transform.Id, writeBack.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors));
    }

    [Fact]
    public void Mapping_node_cannot_feed_write_back()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "mapped", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var mapping = workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 60);
        var writeBack = workflow.AddNode(WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, rank: 70, configurationJson: TargetConfig);
        workflow.AddEdge(source.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, writeBack.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("cannot accept output contract", StringComparison.Ordinal));
    }

    [Fact]
    public void De_identification_upstream_of_write_back_is_refused_even_behind_a_transformation()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "deid", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var deId = workflow.AddNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, rank: 50);
        var writeBack = workflow.AddNode(WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, rank: 70, configurationJson: TargetConfig);
        workflow.AddEdge(source.Id, deId.Id);
        workflow.AddEdge(deId.Id, writeBack.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("cannot write de-identified data", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"dest_sourceConnectionId":""}""")]
    [InlineData("""{"dest_sourceConnectionId":"not-a-guid"}""")]
    public void Write_back_must_name_its_target_connection(string configurationJson)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "no-target", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var writeBack = workflow.AddNode(WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, rank: 70, configurationJson: configurationJson);
        workflow.AddEdge(source.Id, writeBack.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Write_back_fed_by_two_sources_is_refused()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "two-sources", 1);
        var first = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var second = workflow.AddNode(WorkflowNodeTypes.GenericFhirSource, WorkflowNodeCategory.Source, rank: 0);
        var writeBack = workflow.AddNode(WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, rank: 70, configurationJson: TargetConfig);
        workflow.AddEdge(first.Id, writeBack.Id);
        workflow.AddEdge(second.Id, writeBack.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("exactly one source node", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_entry_matches_the_portal_transform_id()
    {
        var item = new DefaultWorkflowNodeCatalog().Find(WorkflowNodeTypes.EhrWriteBackDestination)!;

        item.TransformId.Should().Be("dest-ehr-writeback");
        item.InputContracts.Should().BeEquivalentTo([WorkflowDataContract.ResourceBatch, WorkflowDataContract.NormalizedResourceBatch]);
        item.RequiredConfigurationFields.Should().Equal("dest_sourceConnectionId");
    }
}
