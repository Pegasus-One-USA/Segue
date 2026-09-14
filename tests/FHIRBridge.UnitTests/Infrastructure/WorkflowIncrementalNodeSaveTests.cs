using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

// Exercises the exact load -> mutate -> BumpVersion -> save sequence WorkflowNodeEndpoints.cs runs for
// POST/PUT/DELETE /workflows/{id}/nodes, against a real (in-memory EF) SqlWorkflowDefinitionStore — proving
// the plumbing 2a/2b built (stable node ids across saves, RemoveNode/AddNode with an explicit id) actually
// supports adding/editing/removing one node at a time without disturbing the rest of the graph. The Api
// integration test suite covers the HTTP-layer behavior (catalog validation, 404s, response shapes) but
// can't run in every environment (see WorkflowNodeEndpointsTests.cs remarks); this suite has no such
// dependency.
public sealed class WorkflowIncrementalNodeSaveTests
{
    private static readonly InMemoryDatabaseRoot _root = new();
    private static readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    private static ICurrentUserService CurrentUserAs(string email = "test@example.com")
    {
        var mock = new Mock<ICurrentUserService>();
        mock.Setup(s => s.CurrentUser).Returns(new CurrentUserInfo(null, email, null, Array.Empty<string>(), true));
        return mock.Object;
    }

    private static SqlWorkflowDefinitionStore CreateDefinitionStore(FHIRBridgeDbContext context) =>
        new(context, CurrentUserAs());

    // Reproduces a real bug caught in manual testing: /workflows/build computed its version bump from a
    // WorkflowDefinition read at the TOP of the request (before provisioning sources/destinations/mappings,
    // which can take real time), not from a fresh read taken immediately before the actual save. Once an
    // incremental /workflows/{id}/nodes add landed in between, the whole-graph Save's own version arithmetic
    // collided with a change that had already been persisted, and the save was rejected outright — losing
    // every node that existed only on the canvas (e.g. a source node never sent to the incremental endpoint)
    // since nothing from that failed save ever reached the database. The fix (see WorkflowEndpoints.cs's
    // "/workflows/build" and "PUT /workflows/{id}") is to re-read the current version immediately before
    // building the save, not reuse whatever was loaded earlier in the request.
    [Fact]
    public async Task A_whole_graph_save_succeeds_when_it_rebuilds_its_version_off_a_fresh_read()
    {
        var workflowId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            var empty = new WorkflowDefinition(workflowId, "Mid-build", 1);
            await CreateDefinitionStore(context).SaveAsync(empty, CancellationToken.None);
        }

        // An incremental add (e.g. a Mapping node dropped via a destination's "+") lands first, mirroring
        // WorkflowNodeEndpoints' POST /nodes — bumps the stored row to version 2.
        await using (var context = CreateContext())
        {
            var workflow = await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None);
            workflow!.AddNode("MappingNode", WorkflowNodeCategory.Transform, 10);
            workflow.BumpVersion();
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        // The whole-graph Save now runs — its own request was in flight since before the incremental add
        // (e.g. permission checks / provisioning took real time), but it does the RIGHT thing: re-reads the
        // current version (2) immediately before building, rather than reusing a version read earlier.
        await using (var context = CreateContext())
        {
            var currentVersion = (await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None))!.Version;

            // The canvas already includes both the source (never incrementally persisted) and the mapping
            // node (now carrying its real server id) — a whole-graph rebuild naturally covers both once the
            // version itself doesn't collide.
            var wholeGraph = new WorkflowDefinition(workflowId, "Mid-build", currentVersion + 1);
            wholeGraph.AddNode("SampleSourceNode", WorkflowNodeCategory.Source, 0);
            wholeGraph.AddNode("MappingNode", WorkflowNodeCategory.Transform, 10);

            var save = () => CreateDefinitionStore(context).SaveAsync(wholeGraph, CancellationToken.None);
            await save.Should().NotThrowAsync();
        }

        await using (var assertContext = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(assertContext).GetAsync(workflowId, CancellationToken.None);
            // Both nodes must have survived — this is the exact data loss the bug caused (the source node
            // vanished because the failed save never reached the database at all).
            reloaded!.Nodes.Select(n => n.NodeType).Should().BeEquivalentTo("SampleSourceNode", "MappingNode");
            reloaded.Version.Should().Be(3);
        }
    }

    // The failure mode this fix replaces: reusing a version read BEFORE the incremental add landed makes
    // the whole-graph save collide with a change that already persisted, and reject.
    [Fact]
    public async Task A_whole_graph_save_using_a_stale_pre_incremental_version_is_rejected()
    {
        var workflowId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            var empty = new WorkflowDefinition(workflowId, "Mid-build", 1);
            await CreateDefinitionStore(context).SaveAsync(empty, CancellationToken.None);
        }

        // The whole-graph save's request "started" here — this is the stale version it would have reused
        // under the bug (loaded once, at the top of the request, well before the actual save).
        int staleVersionSeenAtRequestStart;
        await using (var context = CreateContext())
        {
            staleVersionSeenAtRequestStart = (await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None))!.Version;
        }

        // Meanwhile, an incremental add lands and bumps the stored row to version 2.
        await using (var context = CreateContext())
        {
            var workflow = await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None);
            workflow!.AddNode("MappingNode", WorkflowNodeCategory.Transform, 10);
            workflow.BumpVersion();
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var staleWholeGraph = new WorkflowDefinition(workflowId, "Mid-build", staleVersionSeenAtRequestStart + 1);
            staleWholeGraph.AddNode("SampleSourceNode", WorkflowNodeCategory.Source, 0);
            staleWholeGraph.AddNode("MappingNode", WorkflowNodeCategory.Transform, 10);

            var save = () => CreateDefinitionStore(context).SaveAsync(staleWholeGraph, CancellationToken.None);
            await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        // Nothing from the rejected save reached the database — the incremental add's Mapping node is the
        // only thing there, exactly the data loss seen in manual testing.
        await using (var assertContext = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(assertContext).GetAsync(workflowId, CancellationToken.None);
            reloaded!.Nodes.Should().ContainSingle(n => n.NodeType == "MappingNode");
        }
    }

    [Fact]
    public async Task Adding_a_node_to_an_empty_workflow_does_not_require_a_destination()
    {
        var workflowId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            var empty = new WorkflowDefinition(workflowId, "Mid-build", 1);
            await CreateDefinitionStore(context).SaveAsync(empty, CancellationToken.None);
        }

        // Mirrors WorkflowNodeEndpoints' POST /nodes: load, AddNode, BumpVersion, save — no destination
        // exists yet, and nothing here requires one.
        await using (var context = CreateContext())
        {
            var workflow = await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None);
            workflow!.AddNode("SampleSourceNode", WorkflowNodeCategory.Source, 0);
            workflow.BumpVersion();
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(assertContext).GetAsync(workflowId, CancellationToken.None);
            reloaded!.Nodes.Should().ContainSingle(node => node.NodeType == "SampleSourceNode");
            reloaded.Version.Should().Be(2);
        }
    }

    [Fact]
    public async Task A_node_added_incrementally_keeps_its_id_across_a_later_incremental_add()
    {
        var workflowId = Guid.NewGuid();
        Guid firstNodeId;

        await using (var context = CreateContext())
        {
            var empty = new WorkflowDefinition(workflowId, "Mid-build", 1);
            await CreateDefinitionStore(context).SaveAsync(empty, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var workflow = await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None);
            var node = workflow!.AddNode("SampleSourceNode", WorkflowNodeCategory.Source, 0);
            firstNodeId = node.Id;
            workflow.BumpVersion();
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        // A second incremental add (e.g. the destination, dropped moments later in the portal).
        await using (var context = CreateContext())
        {
            var workflow = await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None);
            workflow!.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, 30);
            workflow.BumpVersion();
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(assertContext).GetAsync(workflowId, CancellationToken.None);
            reloaded!.Nodes.Should().HaveCount(2);
            // The whole point of Step 2a/2b: the first node's id must have survived the second, unrelated save.
            reloaded.Nodes.Should().Contain(node => node.Id == firstNodeId && node.NodeType == "SampleSourceNode");
            reloaded.Version.Should().Be(3);
        }
    }

    [Fact]
    public async Task Removing_one_node_incrementally_leaves_the_rest_of_the_graph_untouched()
    {
        var workflowId = Guid.NewGuid();
        Guid keptNodeId;

        await using (var context = CreateContext())
        {
            var workflow = new WorkflowDefinition(workflowId, "Two nodes", 1);
            var source = workflow.AddNode("SampleSourceNode", WorkflowNodeCategory.Source, 0);
            var destination = workflow.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, 30);
            workflow.AddEdge(source.Id, destination.Id);
            keptNodeId = destination.Id;
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        // Mirrors WorkflowNodeEndpoints' DELETE /nodes/{nodeId}: load, RemoveNode, BumpVersion, save.
        await using (var context = CreateContext())
        {
            var workflow = await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None);
            var sourceNodeId = workflow!.Nodes.Single(n => n.NodeType == "SampleSourceNode").Id;
            workflow.RemoveNode(sourceNodeId);
            workflow.BumpVersion();
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(assertContext).GetAsync(workflowId, CancellationToken.None);
            reloaded!.Nodes.Should().ContainSingle(node => node.Id == keptNodeId);
            // The edge referenced the removed node, so it must be gone too (WorkflowDefinition.RemoveNode).
            reloaded.Edges.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Updating_a_node_in_place_keeps_its_id_and_unrelated_nodes_untouched()
    {
        var workflowId = Guid.NewGuid();
        Guid updatedNodeId;
        Guid untouchedNodeId;

        await using (var context = CreateContext())
        {
            var workflow = new WorkflowDefinition(workflowId, "Two nodes", 1);
            var source = workflow.AddNode("SampleSourceNode", WorkflowNodeCategory.Source, 0, displayName: "Original name");
            var destination = workflow.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, 30);
            updatedNodeId = source.Id;
            untouchedNodeId = destination.Id;
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        // Mirrors WorkflowNodeEndpoints' PUT /nodes/{nodeId}: remove-and-readd under the same id with the
        // changed field, everything else carried over from the existing node.
        await using (var context = CreateContext())
        {
            var workflow = await CreateDefinitionStore(context).GetAsync(workflowId, CancellationToken.None);
            var existing = workflow!.Nodes.Single(n => n.Id == updatedNodeId);
            workflow.RemoveNode(updatedNodeId);
            workflow.AddNode(
                existing.NodeType, existing.Category, existing.Rank, existing.SubRank,
                "Renamed", existing.ConfigurationJson, existing.PositionX, existing.PositionY,
                existing.IsEnabled, existing.CheckpointUrlEnabled, updatedNodeId);
            workflow.BumpVersion();
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(assertContext).GetAsync(workflowId, CancellationToken.None);
            reloaded!.Nodes.Should().HaveCount(2);
            reloaded.Nodes.Should().Contain(node => node.Id == updatedNodeId && node.DisplayName == "Renamed");
            reloaded.Nodes.Should().Contain(node => node.Id == untouchedNodeId);
        }
    }
}
