using FHIRBridge.Application.Services.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>
/// V1 → V2 graph conversion. V2 collapsed six granular V1 transform steps into one Transformation node, so
/// this is not a rename — nodes disappear and the edges through them have to be rewired, or converting would
/// sever the only path from source to destination. See plan §8.4.
/// </summary>
public sealed class WorkflowGraphVersionMigrationServiceTests
{
    private sealed class RecordingStore : IWorkflowDefinitionStore
    {
        private readonly List<WorkflowDefinition> _workflows;

        public RecordingStore(params WorkflowDefinition[] workflows) => _workflows = [.. workflows];

        public List<WorkflowDefinition> Saved { get; } = [];

        public Task<WorkflowDefinition> SaveAsync(WorkflowDefinition workflowDefinition, CancellationToken cancellationToken)
        {
            Saved.Add(workflowDefinition);
            return Task.FromResult(workflowDefinition);
        }

        public Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<WorkflowDefinition>>(_workflows);

        public Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken)
            => Task.FromResult(_workflows.FirstOrDefault(workflow => workflow.Id == workflowId));

        public Task DeleteAsync(Guid workflowId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Source → [collapsible steps] → Destination, wired as a chain.</summary>
    private static WorkflowDefinition ChainWorkflow(params (string NodeType, string Json)[] middle)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "V1 pipeline", version: 1);

        var source = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 10);
        var previous = source;
        var rank = 30;

        foreach (var (nodeType, json) in middle)
        {
            var node = workflow.AddNode(nodeType, WorkflowNodeCategory.Transform, rank, configurationJson: json);
            workflow.AddEdge(previous.Id, node.Id);
            previous = node;
            rank++;
        }

        var destination = workflow.AddNode(
            WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(previous.Id, destination.Id);

        return workflow;
    }

    private static WorkflowGraphVersionMigrationService Service(IWorkflowDefinitionStore store) => new(store);

    [Fact]
    public async Task A_graph_with_no_v1_only_nodes_needs_no_conversion()
    {
        var workflow = ChainWorkflow((WorkflowNodeTypes.FhirResourceTransform, "{}"));
        var store = new RecordingStore(workflow);

        var result = await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        store.Saved.Should().BeEmpty();
        result.Workflows.Single().RequiresConversion.Should().BeFalse();
        result.IsSafeToRetireV1.Should().BeTrue();
    }

    [Fact]
    public async Task A_dry_run_reports_what_would_collapse_and_writes_nothing()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, "{}"),
            (WorkflowNodeTypes.Terminology, "{}"));
        var store = new RecordingStore(workflow);

        var result = await Service(store).ConvertAsync(dryRun: true, workflowId: null, CancellationToken.None);

        store.Saved.Should().BeEmpty();
        result.DryRun.Should().BeTrue();

        var report = result.Workflows.Single();
        report.RequiresConversion.Should().BeTrue();
        report.CollapsedNodes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Collapsible_nodes_become_one_transformation_node()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, "{}"),
            (WorkflowNodeTypes.PatientMatching, "{}"),
            (WorkflowNodeTypes.Terminology, "{}"));
        var store = new RecordingStore(workflow);

        await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        var saved = store.Saved.Should().ContainSingle().Subject;
        var transforms = saved.Nodes.Where(node => node.Category == WorkflowNodeCategory.Transform).ToList();

        transforms.Should().ContainSingle();
        transforms[0].NodeType.Should().Be(WorkflowNodeTypes.FhirResourceTransform);
        transforms[0].Rank.Should().Be(WorkflowGraphVersionConverter.TransformationRank);
    }

    /// <summary>The failure this rewiring exists to prevent: collapsing the middle of a chain must not sever
    /// source from destination.</summary>
    [Fact]
    public async Task The_chain_stays_connected_after_collapsing()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, "{}"),
            (WorkflowNodeTypes.Terminology, "{}"));
        var sourceId = workflow.Nodes.Single(node => node.Category == WorkflowNodeCategory.Source).Id;
        var destinationId = workflow.Nodes.Single(node => node.Category == WorkflowNodeCategory.Destination).Id;
        var store = new RecordingStore(workflow);

        await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        var saved = store.Saved.Single();
        var transformId = saved.Nodes.Single(node => node.Category == WorkflowNodeCategory.Transform).Id;

        saved.Edges.Should().HaveCount(2);
        saved.Edges.Should().Contain(edge => edge.FromNodeId == sourceId && edge.ToNodeId == transformId);
        saved.Edges.Should().Contain(edge => edge.FromNodeId == transformId && edge.ToNodeId == destinationId);
    }

    /// <summary>An edge BETWEEN two collapsed nodes would become a self-edge on the survivor, which
    /// WorkflowEdge rejects outright.</summary>
    [Fact]
    public async Task Edges_between_collapsed_nodes_are_dropped_rather_than_becoming_self_edges()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, "{}"),
            (WorkflowNodeTypes.FlattenExtensions, "{}"),
            (WorkflowNodeTypes.DataQualityScoring, "{}"));
        var store = new RecordingStore(workflow);

        await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        var saved = store.Saved.Single();
        saved.Edges.Should().NotContain(edge => edge.FromNodeId == edge.ToNodeId);
        saved.Edges.Should().HaveCount(2);
    }

    /// <summary>Run history, field lineage and issued checkpoint urls reference node ids — reusing the first
    /// collapsed node's id keeps them resolving to the step that replaced it.</summary>
    [Fact]
    public async Task The_replacement_reuses_the_first_collapsed_nodes_id()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, "{}"),
            (WorkflowNodeTypes.Terminology, "{}"));
        var firstCollapsibleId = WorkflowGraphVersionConverter.CollapsibleNodes(workflow)[0].Id;
        var store = new RecordingStore(workflow);

        await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        store.Saved.Single().Nodes
            .Single(node => node.Category == WorkflowNodeCategory.Transform).Id
            .Should().Be(firstCollapsibleId);
    }

    /// <summary>Two configured steps cannot be merged automatically — there is no correct way to decide which
    /// configuration wins, so the graph is reported and left alone.</summary>
    [Fact]
    public async Task Two_configured_collapsible_steps_block_conversion()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, """{ "threshold": 3 }"""),
            (WorkflowNodeTypes.PatientMatching, """{ "strategy": "deterministic" }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        store.Saved.Should().BeEmpty();
        result.Workflows.Single().Blocker.Should().Contain("cannot be merged automatically");
        result.IsSafeToRetireV1.Should().BeFalse();
    }

    /// <summary>Builder bookkeeping keys are not user configuration, so they must not block a clean collapse.</summary>
    [Fact]
    public async Task Builder_bookkeeping_keys_do_not_count_as_configuration()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, """{ "__transformId": "normalize", "__name": "Normalize Data" }"""),
            (WorkflowNodeTypes.Terminology, """{ "__transformId": "terminology" }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        result.Workflows.Single().Blocker.Should().BeNull();
        store.Saved.Should().ContainSingle();
    }

    [Fact]
    public async Task One_configured_step_collapses_and_keeps_its_configuration()
    {
        var workflow = ChainWorkflow(
            (WorkflowNodeTypes.Normalization, "{}"),
            (WorkflowNodeTypes.PatientMatching, """{ "strategy": "deterministic" }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store).ConvertAsync(dryRun: false, workflowId: null, CancellationToken.None);

        result.Workflows.Single().Blocker.Should().BeNull();
        store.Saved.Should().ContainSingle();
    }
}
