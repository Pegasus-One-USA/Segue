using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

public sealed class WorkflowGraphValidatorTests
{
    [Fact]
    public void Validate_rejects_edges_that_do_not_move_to_a_higher_rank()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "invalid-rank", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var transform = workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 60);

        workflow.AddEdge(transform.Id, source.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("violates rank ordering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_nodes_that_do_not_match_catalog_rank()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "invalid-category-rank", 1);

        workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 25);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("does not match catalog rank", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_nodes_that_do_not_match_catalog_category()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "category-mismatch", 1);

        workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Compliance, rank: 60);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("does not match catalog category", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_allows_raw_source_to_mapping_contract_edges()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "raw-source-mapping", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var mapping = workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 60);

        workflow.AddEdge(source.Id, mapping.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_destination_without_mapped_contract()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "bad-contract", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var destination = workflow.AddNode(WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, rank: 70);

        workflow.AddEdge(source.Id, destination.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.Contains("cannot accept output contract", StringComparison.Ordinal)
            || error.Contains("requires mapped records", StringComparison.Ordinal));
    }
}
