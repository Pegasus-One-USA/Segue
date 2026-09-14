using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services.Workflows;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Moq;
using Xunit;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>
/// The migration's safety properties, which the plan (§7) calls non-negotiable: dry-run writes nothing, a node
/// whose ids no longer resolve is reported rather than written as an empty snapshot, a blocked node stops its
/// whole workflow rather than leaving a half-migrated graph, and node ids survive the rewrite.
/// </summary>
public sealed class WorkflowConfigurationMigrationServiceTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DestinationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Captures what SaveAsync was handed, so tests can assert on the rewritten graph.</summary>
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

    private static Mock<IConfigurationRepository> RepositoryResolving(bool source = true, bool destination = true)
    {
        var repository = new Mock<IConfigurationRepository>();

        repository
            .Setup(r => r.GetSourceConnectionAsync(SourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source ? BuildSourceConnection() : null);

        repository
            .Setup(r => r.GetDestinationAsync(DestinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(destination ? BuildDestination() : null);

        return repository;
    }

    private static SourceConnection BuildSourceConnection() =>
        new(
            "Epic Prod",
            SourceSystemType.Epic,
            "https://fhir.example.org/api/FHIR/R4",
            new SourceAuthenticationConfiguration(AuthenticationType.None, null, null, [], null, null, null));

    private static DestinationConfiguration BuildDestination() =>
        new("Analytics SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "dbo");

    private static WorkflowDefinition WorkflowWith(params (string NodeType, WorkflowNodeCategory Category, string Json)[] nodes)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "Epic to SQL", version: 1);
        var rank = 10;
        foreach (var (nodeType, category, json) in nodes)
        {
            workflow.AddNode(nodeType, category, rank, configurationJson: json);
            rank += 10;
        }

        return workflow;
    }

    private static WorkflowConfigurationMigrationService Service(
        IWorkflowDefinitionStore store, IConfigurationRepository repository)
        => new(store, repository);

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source, $$"""{ "sourceConnectionId": "{{SourceId}}" }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store, RepositoryResolving().Object)
            .MigrateAsync(dryRun: true, workflowId: null, CancellationToken.None);

        store.Saved.Should().BeEmpty();
        result.DryRun.Should().BeTrue();
        result.WorkflowsWritten.Should().Be(0);
        result.Workflows.Single().Nodes.Single().Action.Should().Be(WorkflowNodeMigrationAction.Migrate);
    }

    [Fact]
    public async Task A_dry_run_reports_what_each_node_resolved_to()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source, $$"""{ "sourceConnectionId": "{{SourceId}}" }"""),
            ("SqlServerDestinationNode", WorkflowNodeCategory.Destination, $$"""{ "destinationId": "{{DestinationId}}" }"""));

        var result = await Service(new RecordingStore(workflow), RepositoryResolving().Object)
            .MigrateAsync(dryRun: true, workflowId: null, CancellationToken.None);

        var nodes = result.Workflows.Single().Nodes;
        nodes[0].ResolvedReferences.Should().ContainSingle().Which.Should().Contain("Epic Prod");
        nodes[1].ResolvedReferences.Should().ContainSingle().Which.Should().Contain("Analytics SQL");
    }

    /// <summary>The corruption this whole plan exists to prevent: a node pointing at a master that has gone must
    /// never be written as an empty snapshot.</summary>
    [Fact]
    public async Task A_node_whose_id_no_longer_resolves_is_blocked_not_written()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source, $$"""{ "sourceConnectionId": "{{SourceId}}" }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store, RepositoryResolving(source: false).Object)
            .MigrateAsync(dryRun: false, workflowId: null, CancellationToken.None);

        store.Saved.Should().BeEmpty();
        result.WorkflowsWritten.Should().Be(0);

        var node = result.Workflows.Single().Nodes.Single();
        node.Action.Should().Be(WorkflowNodeMigrationAction.Blocked);
        node.Blocker.Should().Contain("does not resolve");
    }

    /// <summary>A half-migrated graph is worse than an untouched one, so one blocked node stops the workflow.</summary>
    [Fact]
    public async Task One_blocked_node_stops_the_whole_workflow()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source, $$"""{ "sourceConnectionId": "{{SourceId}}" }"""),
            ("SqlServerDestinationNode", WorkflowNodeCategory.Destination, $$"""{ "destinationId": "{{DestinationId}}" }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store, RepositoryResolving(destination: false).Object)
            .MigrateAsync(dryRun: false, workflowId: null, CancellationToken.None);

        store.Saved.Should().BeEmpty();
        result.Workflows.Single().IsBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task An_unparseable_id_blocks_rather_than_being_dropped()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source, """{ "sourceConnectionId": "not-a-guid" }"""));

        var result = await Service(new RecordingStore(workflow), RepositoryResolving().Object)
            .MigrateAsync(dryRun: true, workflowId: null, CancellationToken.None);

        var node = result.Workflows.Single().Nodes.Single();
        node.Action.Should().Be(WorkflowNodeMigrationAction.Blocked);
        node.Blocker.Should().Contain("not a valid id");
    }

    [Fact]
    public async Task Applying_rewrites_the_node_into_the_enveloped_shape()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source,
                $$"""{ "sourceConnectionId": "{{SourceId}}", "resourceTypes": ["Patient"] }"""));
        var store = new RecordingStore(workflow);

        await Service(store, RepositoryResolving().Object)
            .MigrateAsync(dryRun: false, workflowId: null, CancellationToken.None);

        var saved = store.Saved.Should().ContainSingle().Subject;
        var root = JsonDocument.Parse(saved.Nodes.Single().ConfigurationJson).RootElement;

        WorkflowNodeConfigurationEnvelope.IsEnveloped(root).Should().BeTrue();

        // Settings survive intact under config...
        var settings = WorkflowNodeConfigurationEnvelope.ResolveSettings(root);
        settings.GetProperty("sourceConnectionId").GetString().Should().Be(SourceId.ToString());
        settings.GetProperty("resourceTypes").EnumerateArray().Single().GetString().Should().Be("Patient");

        // ...and provenance records where they came from.
        root.GetProperty("ref").GetProperty("sourceConnectionId").GetString().Should().Be(SourceId.ToString());
    }

    /// <summary>Edges, run history, field lineage and issued checkpoint urls all reference node ids. Minting new
    /// ones during the rewrite would orphan every one of them.</summary>
    [Fact]
    public async Task Node_ids_and_edges_survive_the_rewrite()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source, $$"""{ "sourceConnectionId": "{{SourceId}}" }"""),
            ("SqlServerDestinationNode", WorkflowNodeCategory.Destination, $$"""{ "destinationId": "{{DestinationId}}" }"""));
        var originalIds = workflow.Nodes.Select(node => node.Id).ToArray();
        workflow.AddEdge(originalIds[0], originalIds[1]);

        var store = new RecordingStore(workflow);
        await Service(store, RepositoryResolving().Object)
            .MigrateAsync(dryRun: false, workflowId: null, CancellationToken.None);

        var saved = store.Saved.Single();
        saved.Nodes.Select(node => node.Id).Should().BeEquivalentTo(originalIds);

        var edge = saved.Edges.Should().ContainSingle().Subject;
        edge.FromNodeId.Should().Be(originalIds[0]);
        edge.ToNodeId.Should().Be(originalIds[1]);
    }

    /// <summary>Re-running after a partial apply must be safe and honest, not double-wrap.</summary>
    [Fact]
    public async Task An_already_enveloped_node_is_left_alone()
    {
        var workflow = WorkflowWith(
            ("EpicSourceNode", WorkflowNodeCategory.Source,
                $$"""{ "ref": { "sourceConnectionId": "{{SourceId}}" }, "config": { "sourceConnectionId": "{{SourceId}}" } }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store, RepositoryResolving().Object)
            .MigrateAsync(dryRun: false, workflowId: null, CancellationToken.None);

        store.Saved.Should().BeEmpty();
        result.Workflows.Single().Nodes.Single().Action.Should().Be(WorkflowNodeMigrationAction.AlreadyMigrated);
    }

    [Fact]
    public async Task A_node_carrying_no_master_ids_is_enveloped_as_is()
    {
        var workflow = WorkflowWith(
            ("FhirResourceTransformNode", WorkflowNodeCategory.Transform, """{ "rules": [] }"""));
        var store = new RecordingStore(workflow);

        var result = await Service(store, RepositoryResolving().Object)
            .MigrateAsync(dryRun: false, workflowId: null, CancellationToken.None);

        result.Workflows.Single().Nodes.Single().Action.Should().Be(WorkflowNodeMigrationAction.EnvelopeOnly);
        store.Saved.Should().ContainSingle();
    }
}
