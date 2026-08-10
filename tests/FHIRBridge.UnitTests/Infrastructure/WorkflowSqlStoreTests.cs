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

// Scenario A: proves designed graphs and run history durably round-trip through the control-plane
// database via the SQL stores. Uses the EF in-memory provider with a shared root so a fresh context
// (a new "request") reads back what a previous context wrote.
public sealed class WorkflowSqlStoreTests
{
    // static, not per-instance: xUnit gives every [Fact] its own class instance, so instance fields here
    // would mean a fresh InMemory database name (and a fresh EF internal service provider) per test method —
    // EF warns-as-error once more than 20 such distinct configurations exist across a full run, and this
    // suite is close enough to that ceiling that this class alone tipped it over. Sharing one database across
    // every Fact here is safe: each test already keys its own rows with fresh random GUIDs (WorkflowId,
    // WorkflowRunId, TransformationRule.Id, ...), so there's no cross-test collision risk — xUnit runs
    // methods within one class sequentially by default anyway.
    private static readonly InMemoryDatabaseRoot _root = new();
    private static readonly string _databaseName = Guid.NewGuid().ToString();

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
    public async Task Run_store_replaces_an_AwaitingBulkExport_placeholder_once_the_resume_completes()
    {
        // Mirrors the real bulk-export pause/resume flow: RankedWorkflowOrchestrator.ExecuteAsync persists a
        // "Running" placeholder, then AwaitBulkExport() moves it to "AwaitingBulkExport" from the SAME tracked
        // instance/scope — both writes go through the ChangeTracker fast path. The RESUME, though, happens in a
        // brand-new scope/DbContext (a later BulkExportPollWorker tick, quite possibly a different process): it
        // fetches the run via GetAsync (AsNoTracking), completes it in memory, and saves from a context that has
        // never tracked this run at all — exercising the "existing" branch below, not the ChangeTracker branch.
        var runId = Guid.NewGuid();
        var run = new WorkflowRun(runId, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await using (var context = CreateContext())
        {
            await new SqlWorkflowRunStore(context).SaveAsync(run, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            run.AwaitBulkExport();
            await new SqlWorkflowRunStore(context).SaveAsync(run, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var stillPaused = await new SqlWorkflowRunStore(assertContext).GetAsync(runId, CancellationToken.None);
            stillPaused!.Status.Should().Be(WorkflowRunStatus.AwaitingBulkExport);
        }

        // The resume: a fresh scope loads the AwaitingBulkExport row untracked, finishes the run, and saves it back.
        await using (var resumeContext = CreateContext())
        {
            var store = new SqlWorkflowRunStore(resumeContext);
            var resumed = await store.GetAsync(runId, CancellationToken.None);
            resumed!.Succeed(DateTimeOffset.UtcNow);
            await store.SaveAsync(resumed, CancellationToken.None);
        }

        await using (var assertContext = CreateContext())
        {
            var reloaded = await new SqlWorkflowRunStore(assertContext).GetAsync(runId, CancellationToken.None);
            reloaded!.Status.Should().Be(WorkflowRunStatus.Succeeded);
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

            counts.Should().HaveCount(7);
            counts[WorkflowRunStatus.Succeeded].Should().Be(2);
            counts[WorkflowRunStatus.Failed].Should().Be(1);
            counts[WorkflowRunStatus.Running].Should().Be(1);
            counts[WorkflowRunStatus.Pending].Should().Be(0);
            counts[WorkflowRunStatus.Cancelled].Should().Be(0);
            counts[WorkflowRunStatus.PartialSuccess].Should().Be(0);
            counts[WorkflowRunStatus.AwaitingBulkExport].Should().Be(0);
        }
    }

    // Scenario B: a Field-scoped TransformationRule authored with no resource type / destination field of
    // its own — matched purely by source field name, so it applies wherever that source field is mapped, on
    // any resource type, into any destination column. Previously GetFieldScopedAsync required an exact match
    // on both, so a rule like this could never match anything at all. Both cases live in one Fact (rather
    // than one each) reusing this class's own CreateContext() — every additional [Fact] in an EF-InMemory
    // test class gets its own fresh database name (xUnit gives each Fact a new class instance), and EF
    // warns-as-error once more than 20 such distinct configurations exist across a full test run; this
    // suite is already close to that ceiling.
    [Fact]
    public async Task Field_rules_with_no_resource_type_match_by_source_field_alone_but_a_specific_rule_still_wins()
    {
        var blanket = new TransformationRule(
            TransformScope.Field, TransformNodeType.DateTimeFormat, "{}",
            resourceType: null, destinationField: null, sourceField: "birthDate");
        blanket.MarkCreated("test@example.com");

        await using (var context = CreateContext())
        {
            context.TransformationRules.Add(blanket);
            await context.SaveChangesAsync();
        }

        await using (var readContext = CreateContext())
        {
            var repository = new EfTransformationRuleRepository(readContext);
            var forPatient = await repository.GetFieldScopedAsync("Patient", "BirthDate", null, "birthDate", CancellationToken.None);
            var forPractitioner = await repository.GetFieldScopedAsync("Practitioner", "DOB", null, "birthDate", CancellationToken.None);

            forPatient.Should().ContainSingle().Which.Id.Should().Be(blanket.Id);
            forPractitioner.Should().ContainSingle().Which.Id.Should().Be(blanket.Id);
        }

        var specific = new TransformationRule(
            TransformScope.Field, TransformNodeType.HashingMasking, "{}",
            resourceType: "Patient", destinationField: "BirthDate", sourceField: "birthDate");
        specific.MarkCreated("test@example.com");

        await using (var context = CreateContext())
        {
            context.TransformationRules.Add(specific);
            await context.SaveChangesAsync();
        }

        await using (var readContext = CreateContext())
        {
            var repository = new EfTransformationRuleRepository(readContext);
            var rules = await repository.GetFieldScopedAsync("Patient", "BirthDate", null, "birthDate", CancellationToken.None);

            rules.Should().HaveCount(2, "the repository returns every candidate — the resolver picks the most specific one");
        }
    }
}
