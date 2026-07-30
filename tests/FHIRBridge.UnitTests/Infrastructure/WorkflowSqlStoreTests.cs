using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

// Scenario A: proves designed graphs and run history durably round-trip through the control-plane
// database via the SQL stores. Uses the EF in-memory provider with a shared root so a fresh context
// (a new "request") reads back what a previous context wrote.
public sealed class WorkflowSqlStoreTests
{
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    private static ICurrentUserService CurrentUserAs(string email)
    {
        var mock = new Mock<ICurrentUserService>();
        mock.Setup(s => s.CurrentUser).Returns(new CurrentUserInfo(null, email, null, Array.Empty<string>(), true));
        return mock.Object;
    }

    private static SqlWorkflowDefinitionStore CreateDefinitionStore(FHIRBridgeDbContext context, string actorEmail = "test@example.com") =>
        new(context, CurrentUserAs(actorEmail));

    [Fact]
    public async Task Definition_store_round_trips_nodes_edges_and_configuration()
    {
        var definitionId = Guid.NewGuid();
        var workflow = new WorkflowDefinition(definitionId, "Epic -> SQL", 1);
        var source = workflow.AddNode("EpicSourceNode", WorkflowNodeCategory.Source, 0);
        var destination = workflow.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, 30);
        workflow.AddEdge(source.Id, destination.Id);
        workflow.AddNodeConfiguration(destination.Id, "table", "dbo.Patient");

        await using (var context = CreateContext())
        {
            await CreateDefinitionStore(context).SaveAsync(workflow, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(context).GetAsync(definitionId, CancellationToken.None);

            reloaded.Should().NotBeNull();
            reloaded!.Name.Should().Be("Epic -> SQL");
            reloaded.Nodes.Should().HaveCount(2);
            reloaded.Edges.Should().ContainSingle();
            reloaded.Nodes.Single(node => node.NodeType == "SqlServerDestinationNode")
                .Configuration.Should().ContainSingle(configuration =>
                    configuration.Key == "table" && configuration.Value == "dbo.Patient");
        }
    }

    [Fact]
    public async Task Definition_store_save_replaces_the_existing_graph()
    {
        var definitionId = Guid.NewGuid();

        var version1 = new WorkflowDefinition(definitionId, "v1", 1);
        var v1Source = version1.AddNode("EpicSourceNode", WorkflowNodeCategory.Source, 0);
        var v1Destination = version1.AddNode("CsvDestinationNode", WorkflowNodeCategory.Destination, 30);
        version1.AddEdge(v1Source.Id, v1Destination.Id);

        await using (var context = CreateContext())
        {
            await CreateDefinitionStore(context).SaveAsync(version1, CancellationToken.None);
        }

        // Same id, an entirely new graph with fresh node ids — mirrors a PUT rebuilding the definition.
        var version2 = new WorkflowDefinition(definitionId, "v2", 1);
        var v2Source = version2.AddNode("SampleSourceNode", WorkflowNodeCategory.Source, 0);
        var v2Destination = version2.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, 30);
        version2.AddEdge(v2Source.Id, v2Destination.Id);

        await using (var context = CreateContext())
        {
            await CreateDefinitionStore(context).SaveAsync(version2, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var reloaded = await CreateDefinitionStore(assertContext).GetAsync(definitionId, CancellationToken.None);

            reloaded!.Name.Should().Be("v2");
            reloaded.Nodes.Select(node => node.NodeType)
                .Should().BeEquivalentTo("SampleSourceNode", "SqlServerDestinationNode");

            // The v1 graph must be fully gone, not merged.
            assertContext.WorkflowNodes.Count().Should().Be(2);
            assertContext.WorkflowEdges.Count().Should().Be(1);
        }
    }

    [Fact]
    public async Task Definition_store_save_stamps_created_once_and_updated_on_every_later_save()
    {
        var definitionId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            await CreateDefinitionStore(context, "creator@example.com")
                .SaveAsync(new WorkflowDefinition(definitionId, "v1", 1), CancellationToken.None);
        }

        DateTime firstCreatedOnUtc;
        await using (var context = CreateContext())
        {
            var afterFirstSave = await CreateDefinitionStore(context).GetAsync(definitionId, CancellationToken.None);
            afterFirstSave!.CreatedBy.Should().Be("creator@example.com");
            afterFirstSave.UpdatedOnUtc.Should().BeNull();
            afterFirstSave.UpdatedBy.Should().BeNull();
            firstCreatedOnUtc = afterFirstSave.CreatedOnUtc;
        }

        // Same id, a fresh graph — mirrors a PUT rebuilding the definition (an edit, not a first save).
        await using (var context = CreateContext())
        {
            await CreateDefinitionStore(context, "editor@example.com")
                .SaveAsync(new WorkflowDefinition(definitionId, "v2", 1), CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var afterEdit = await CreateDefinitionStore(assertContext).GetAsync(definitionId, CancellationToken.None);

            afterEdit!.CreatedOnUtc.Should().Be(firstCreatedOnUtc);
            afterEdit.CreatedBy.Should().Be("creator@example.com");
            afterEdit.UpdatedOnUtc.Should().NotBeNull();
            afterEdit.UpdatedBy.Should().Be("editor@example.com");
        }
    }

    [Fact]
    public async Task Run_store_round_trips_run_with_its_node_timeline()
    {
        var runId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var run = new WorkflowRun(runId, definitionId, DateTimeOffset.UtcNow);
        var nodeRun = new WorkflowNodeRun(
            Guid.NewGuid(), runId, Guid.NewGuid(), "SqlServerDestinationNode", 30, 0, DateTimeOffset.UtcNow);
        nodeRun.Succeed("{\"table\":\"dbo.Observation\",\"written\":14}", DateTimeOffset.UtcNow);
        run.AddNodeRun(nodeRun);
        run.Succeed(DateTimeOffset.UtcNow);

        await using (var context = CreateContext())
        {
            await new SqlWorkflowRunStore(context).SaveAsync(run, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var store = new SqlWorkflowRunStore(context);

            var reloaded = await store.GetAsync(runId, CancellationToken.None);
            reloaded.Should().NotBeNull();
            reloaded!.Status.Should().Be(WorkflowRunStatus.Succeeded);
            reloaded.NodeRuns.Should().ContainSingle();
            reloaded.NodeRuns.Single().LineageJson.Should().Contain("written");

            var byDefinition = await store.ListByDefinitionAsync(definitionId, CancellationToken.None);
            byDefinition.Should().ContainSingle(persisted => persisted.Id == runId);
        }
    }

    [Fact]
    public async Task Run_store_save_is_idempotent()
    {
        var runId = Guid.NewGuid();
        var run = new WorkflowRun(runId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        run.Succeed(DateTimeOffset.UtcNow);

        await using (var context = CreateContext())
        {
            var store = new SqlWorkflowRunStore(context);
            await store.SaveAsync(run, CancellationToken.None);
            await store.SaveAsync(run, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            assertContext.WorkflowRuns.Count(persisted => persisted.Id == runId).Should().Be(1);
        }
    }

    [Fact]
    public async Task Run_store_status_counts_reflect_every_run_and_zero_fill_unrepresented_statuses()
    {
        var definitionId = Guid.NewGuid();

        var succeeded1 = new WorkflowRun(Guid.NewGuid(), definitionId, DateTimeOffset.UtcNow);
        succeeded1.Succeed(DateTimeOffset.UtcNow);
        var succeeded2 = new WorkflowRun(Guid.NewGuid(), definitionId, DateTimeOffset.UtcNow);
        succeeded2.Succeed(DateTimeOffset.UtcNow);
        var failed = new WorkflowRun(Guid.NewGuid(), definitionId, DateTimeOffset.UtcNow);
        failed.Fail("boom", DateTimeOffset.UtcNow);
        var running = new WorkflowRun(Guid.NewGuid(), definitionId, DateTimeOffset.UtcNow);

        await using (var context = CreateContext())
        {
            var store = new SqlWorkflowRunStore(context);
            await store.SaveAsync(succeeded1, CancellationToken.None);
            await store.SaveAsync(succeeded2, CancellationToken.None);
            await store.SaveAsync(failed, CancellationToken.None);
            await store.SaveAsync(running, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var counts = await new SqlWorkflowRunStore(assertContext).GetStatusCountsAsync(CancellationToken.None);

            counts.Should().HaveCount(5);
            counts[WorkflowRunStatus.Succeeded].Should().Be(2);
            counts[WorkflowRunStatus.Failed].Should().Be(1);
            counts[WorkflowRunStatus.Running].Should().Be(1);
            counts[WorkflowRunStatus.Pending].Should().Be(0);
            counts[WorkflowRunStatus.Cancelled].Should().Be(0);
        }
    }
}
