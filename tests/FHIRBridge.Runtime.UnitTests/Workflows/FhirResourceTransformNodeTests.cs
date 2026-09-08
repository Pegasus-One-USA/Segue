using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Guards the isolation property behind V2's own <see cref="WorkflowNodeTypes.FhirResourceTransform"/> node.
///
/// V2's "Transformation" chain step used to persist as the shared NormalizationNode — the same node type V1's
/// "Normalize Data" step uses — which meant the FHIR-resource rule engine this node will carry would have
/// executed inside V1 pipelines too. These tests assert the separation is real (distinct executors, distinct
/// node types) and that the new node still slots into the chain the graph validator accepts, so a later slice
/// adding the rule engine can only ever change V2's behavior.
/// </summary>
public sealed class FhirResourceTransformNodeTests
{
    [Fact]
    public void The_transformation_node_is_a_distinct_node_type_from_v1s_normalization_node()
    {
        WorkflowNodeTypes.FhirResourceTransform.Should().NotBe(
            WorkflowNodeTypes.Normalization,
            "V1's Normalize Data step keeps NormalizationNode; the rule engine V2 will hang off this node must "
            + "never resolve for a V1 graph");

        new FhirResourceTransformNodeExecutor().NodeType.Should().Be(WorkflowNodeTypes.FhirResourceTransform);
        new NormalizationNodeExecutor().NodeType.Should().Be(WorkflowNodeTypes.Normalization);
    }

    [Fact]
    public void The_executor_registry_resolves_each_node_type_to_its_own_executor()
    {
        var registry = new WorkflowNodeExecutorRegistry(
            [new FhirResourceTransformNodeExecutor(), new NormalizationNodeExecutor()]);

        registry.GetRequired(WorkflowNodeTypes.FhirResourceTransform)
            .Should().BeOfType<FhirResourceTransformNodeExecutor>();
        registry.GetRequired(WorkflowNodeTypes.Normalization)
            .Should().BeOfType<NormalizationNodeExecutor>();
    }

    [Fact]
    public void Validate_accepts_the_full_v2_chain_in_execution_order()
    {
        // Transformation(34) -> De-identification(50) -> Mapping(60) -> Destination(70). Rank must increase
        // across every edge, which is what pins the new node's catalog rank between PatientMatching and
        // De-identification rather than anywhere convenient.
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "v2-chain", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var transformation = workflow.AddNode(WorkflowNodeTypes.FhirResourceTransform, WorkflowNodeCategory.Transform, rank: 34);
        var deIdentification = workflow.AddNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, rank: 50);
        var mapping = workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 60);
        var destination = workflow.AddNode(WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, rank: 70);

        workflow.AddEdge(source.Id, transformation.Id);
        workflow.AddEdge(transformation.Id, deIdentification.Id);
        workflow.AddEdge(deIdentification.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, destination.Id);

        new WorkflowGraphValidator().Validate(workflow).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkflowNodeTypes.FhirRepositoryDestination)]
    [InlineData(WorkflowNodeTypes.MedplumDestination)]
    [InlineData(WorkflowNodeTypes.AzureFhirServiceDestination)]
    public void Whole_resource_fhir_destinations_need_no_upstream_mapping_node(string destinationNodeType)
    {
        // All three persist the resource itself (MappingNodeExecutor's wholeResourceFhir branch), so requiring
        // a Mapping node in front only bought an inert node in the graph. Previously only FhirRepository was
        // exempt, which left Medplum/Azure FHIR graphs carrying one.
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "fhir-direct", 1);
        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0);
        var transformation = workflow.AddNode(WorkflowNodeTypes.FhirResourceTransform, WorkflowNodeCategory.Transform, rank: 34);
        var destination = workflow.AddNode(destinationNodeType, WorkflowNodeCategory.Destination, rank: 70);

        workflow.AddEdge(source.Id, transformation.Id);
        workflow.AddEdge(transformation.Id, destination.Id);

        new WorkflowGraphValidator().Validate(workflow).IsValid.Should().BeTrue();
    }
}
