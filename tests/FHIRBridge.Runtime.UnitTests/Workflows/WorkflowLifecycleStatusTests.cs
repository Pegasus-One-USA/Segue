using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Draft / Ready / Disabled is derived from the graph rather than stored — these pin the decision table,
/// and in particular that a workflow cannot keep claiming Ready once its destination node is gone.
/// </summary>
public sealed class WorkflowLifecycleStatusTests
{
    private static WorkflowDefinition NewWorkflow(bool isEnabled = true) =>
        new(Guid.NewGuid(), "Epic Prod to Analytics SQL", version: 1, isEnabled);

    private static void AddDestination(WorkflowDefinition workflow) =>
        workflow.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 70);

    private static void AddSource(WorkflowDefinition workflow) =>
        workflow.AddNode("EpicSourceNode", WorkflowNodeCategory.Source, rank: 10);

    [Fact]
    public void A_workflow_with_no_nodes_is_Draft()
    {
        NewWorkflow().LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Draft);
    }

    [Fact]
    public void A_source_alone_is_still_Draft_because_there_is_nowhere_to_write()
    {
        var workflow = NewWorkflow();
        AddSource(workflow);

        workflow.LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Draft);
    }

    [Fact]
    public void Adding_a_destination_makes_it_Ready()
    {
        var workflow = NewWorkflow();
        AddSource(workflow);
        AddDestination(workflow);

        workflow.HasDestination.Should().BeTrue();
        workflow.LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Ready);
    }

    [Fact]
    public void Disabled_outranks_Ready_because_pausing_is_an_explicit_decision()
    {
        var workflow = NewWorkflow(isEnabled: false);
        AddSource(workflow);
        AddDestination(workflow);

        workflow.LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Disabled);
    }

    [Fact]
    public void Disabled_also_outranks_Draft_so_a_paused_incomplete_workflow_still_reads_as_paused()
    {
        var workflow = NewWorkflow(isEnabled: false);

        workflow.LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Disabled);
    }

    /// <summary>The reason the status is derived and not stored: a projection that drops the destination node
    /// must report Draft, where a persisted "Ready" would have survived and lied about the graph.</summary>
    [Fact]
    public void Losing_the_destination_node_drops_it_back_to_Draft()
    {
        var workflow = NewWorkflow();
        AddSource(workflow);
        AddDestination(workflow);
        workflow.LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Ready);

        var withoutDestination = workflow.WithNodesAndEdges(
            workflow.Nodes.Where(node => node.Category != WorkflowNodeCategory.Destination).ToList(),
            workflow.Edges.ToList());

        withoutDestination.HasDestination.Should().BeFalse();
        withoutDestination.LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Draft);
    }

    /// <summary>Enabling is not "restore whatever it was" — it recomputes, so an incomplete workflow that is
    /// re-enabled lands on Draft rather than Ready. The portal's optimistic toggle mirrors this.</summary>
    [Fact]
    public void Re_enabling_an_incomplete_workflow_lands_on_Draft_not_Ready()
    {
        var workflow = NewWorkflow(isEnabled: false);
        AddSource(workflow);

        workflow.Activate();

        workflow.LifecycleStatus.Should().Be(WorkflowLifecycleStatus.Draft);
    }
}
