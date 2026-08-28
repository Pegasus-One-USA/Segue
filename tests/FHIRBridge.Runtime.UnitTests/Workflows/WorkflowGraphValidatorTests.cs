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

    /// <summary>
    /// Reconstructs the exact node/rank/edge shape of the production workflow "Epic | ehrLaunch | PatientOnly"
    /// (WorkflowDefinitions.Id 8C8CFA21-01EF-4C7D-834F-2E22CC367642 — EpicSourceNode rank 0 -&gt; MappingNode rank 60
    /// -&gt; SqlServerDestinationNode rank 70, chained by two WorkflowEdges rows) pulled from the database, so a
    /// future catalog/validator change that would silently break this specific, already-running production graph
    /// fails a unit test instead of only surfacing at the next real run.
    /// </summary>
    [Fact]
    public void Validate_accepts_the_production_epic_ehr_launch_patient_only_workflow_shape()
    {
        var workflow = new WorkflowDefinition(Guid.Parse("8C8CFA21-01EF-4C7D-834F-2E22CC367642"), "Epic | ehrLaunch | PatientOnly", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, displayName: "ehr Launch");
        var mapping = workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 60, displayName: "Field Mapping");
        var destination = workflow.AddNode(WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, rank: 70, displayName: "SQL Server");

        workflow.AddEdge(source.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, destination.Id);

        var result = new WorkflowGraphValidator().Validate(workflow);

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors));
    }
}
