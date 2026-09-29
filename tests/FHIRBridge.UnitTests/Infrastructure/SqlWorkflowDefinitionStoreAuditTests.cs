using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services.Workflows.Numbering;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Workflow authoring is audited by the store rather than by AuditingSaveChangesInterceptor, because every save
/// is a delete-and-re-add that EF reports as "Added" — the generic mechanism would log every edit as "Created"
/// plus a spurious "Deleted". These tests pin the two things that makes non-obvious: an edit is an Updated (not a
/// Created), and node-level rows are DIFFED rather than re-emitted for the whole graph.
/// </summary>
public sealed class SqlWorkflowDefinitionStoreAuditTests
{
    private readonly string _databaseName = SharedInMemoryDatabase.NewDatabaseName();
    private readonly List<AuditEntry> _entries = [];

    private FHIRBridgeDbContext CreateContext()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        return new FHIRBridgeDbContext(
            SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(
                _databaseName, new AuditingSaveChangesInterceptor(currentUser.Object)));
    }

    private SqlWorkflowDefinitionStore CreateStore(FHIRBridgeDbContext context)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        var numbers = new Mock<IWorkflowNumberGenerator>();
        numbers.Setup(x => x.NextAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var governance = new Mock<IGovernanceLogger>();
        governance
            .Setup(g => g.LogAuditAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((entry, _) => _entries.Add(entry))
            .Returns(Task.CompletedTask);

        return new SqlWorkflowDefinitionStore(
            context, currentUser.Object, numbers.Object, logger: null, governanceLogger: governance.Object);
    }

    /// <summary>Config carrying the canvas id the API stamps — the identity node diffing keys on.</summary>
    private static string NodeConfig(string canvasNodeId, string body = "") =>
        $$"""{"canvasNodeId":"{{canvasNodeId}}"{{body}}}""";

    private static WorkflowDefinition BuildWorkflow(Guid id, string name, params (string Canvas, string Body)[] nodes)
    {
        var workflow = new WorkflowDefinition(id, name, version: 1);
        foreach (var (canvas, body) in nodes)
        {
            workflow.AddNode(
                "SourceNode", WorkflowNodeCategory.Source, rank: 1,
                displayName: canvas, configurationJson: NodeConfig(canvas, body));
        }

        return workflow;
    }

    [Fact]
    public async Task Creating_a_workflow_writes_one_Created_row()
    {
        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.SaveAsync(BuildWorkflow(Guid.NewGuid(), "Nightly Epic Sync", ("n1", "")), CancellationToken.None);

        var row = _entries.Should().ContainSingle().Subject;
        row.Action.Should().Be("Created");
        row.EntityType.Should().Be(nameof(WorkflowDefinition));
        row.EntityName.Should().Be("Nightly Epic Sync");
        row.OldValueJson.Should().BeNull("nothing preceded a create");
    }

    [Fact]
    public async Task Editing_a_workflow_writes_Updated_not_Created_and_no_spurious_Deleted()
    {
        // The delete-and-re-add trap: EF reports "Added" for this save, so a naive implementation would log a
        // Create here — and a Delete from the internal half that clears the old graph.
        var id = Guid.NewGuid();
        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.SaveAsync(BuildWorkflow(id, "Nightly Epic Sync", ("n1", "")), CancellationToken.None);
        _entries.Clear();

        await store.SaveAsync(BuildWorkflow(id, "Nightly Epic Sync v2", ("n1", "")), CancellationToken.None);

        var workflowRows = _entries.Where(x => x.EntityType == nameof(WorkflowDefinition)).ToList();
        workflowRows.Should().ContainSingle().Which.Action.Should().Be("Updated");
        _entries.Should().NotContain(x => x.Action == "Deleted");
        workflowRows[0].OldValueJson.Should().Contain("Nightly Epic Sync");
        workflowRows[0].NewValueJson.Should().Contain("Nightly Epic Sync v2");
    }

    [Fact]
    public async Task Deleting_a_workflow_writes_a_Deleted_row_that_still_names_it()
    {
        // The delete is physical and cascades, so this row is the only surviving record it existed.
        var id = Guid.NewGuid();
        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.SaveAsync(BuildWorkflow(id, "Nightly Epic Sync", ("n1", "")), CancellationToken.None);
        _entries.Clear();

        await store.DeleteAsync(id, CancellationToken.None);

        var row = _entries.Should().ContainSingle().Subject;
        row.Action.Should().Be("Deleted");
        row.EntityName.Should().Be("Nightly Epic Sync");
        row.OldValueJson.Should().Contain("Nightly Epic Sync");
        row.NewValueJson.Should().BeNull();
    }

    [Fact]
    public async Task Editing_one_node_of_a_large_workflow_writes_one_node_row_not_one_per_node()
    {
        // Without diffing, the delete-and-re-add would emit a row for all twenty nodes on every save.
        var id = Guid.NewGuid();
        var before = Enumerable.Range(0, 20).Select(i => ($"n{i}", "")).ToArray();
        var after = Enumerable.Range(0, 20)
            .Select(i => ($"n{i}", i == 7 ? ",\"field\":\"changed\"" : "")).ToArray();

        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.SaveAsync(BuildWorkflow(id, "Big", before), CancellationToken.None);
        _entries.Clear();

        await store.SaveAsync(BuildWorkflow(id, "Big", after), CancellationToken.None);

        var nodeRows = _entries.Where(x => x.EntityType == nameof(WorkflowNode)).ToList();
        nodeRows.Should().ContainSingle("only one node's configuration actually changed");
        nodeRows[0].Action.Should().Be("Updated");
        nodeRows[0].EntityId.Should().Be("n7");
    }

    [Fact]
    public async Task Re_saving_an_unchanged_graph_writes_no_node_rows()
    {
        var id = Guid.NewGuid();
        var nodes = new[] { ("n1", ""), ("n2", "") };

        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.SaveAsync(BuildWorkflow(id, "Steady", nodes), CancellationToken.None);
        _entries.Clear();

        await store.SaveAsync(BuildWorkflow(id, "Steady", nodes), CancellationToken.None);

        _entries.Should().NotContain(x => x.EntityType == nameof(WorkflowNode));
    }

    [Fact]
    public async Task Adding_and_removing_nodes_is_recorded_per_node()
    {
        var id = Guid.NewGuid();
        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.SaveAsync(BuildWorkflow(id, "Graph", ("n1", ""), ("n2", "")), CancellationToken.None);
        _entries.Clear();

        await store.SaveAsync(BuildWorkflow(id, "Graph", ("n1", ""), ("n3", "")), CancellationToken.None);

        var nodeRows = _entries.Where(x => x.EntityType == nameof(WorkflowNode)).ToList();
        nodeRows.Should().HaveCount(2);
        nodeRows.Should().ContainSingle(x => x.Action == "Created" && x.EntityId == "n3");
        nodeRows.Should().ContainSingle(x => x.Action == "Deleted" && x.EntityId == "n2");
    }

    [Fact]
    public async Task A_nodes_full_configuration_survives_into_the_audit_row_untruncated()
    {
        // ConfigurationJson IS the field mapping being audited — a clipped one would be worse than none.
        var id = Guid.NewGuid();
        var longMapping = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"field{i}\":\"$.path.to.value{i}\""));

        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.SaveAsync(BuildWorkflow(id, "Mapped", ("n1", "")), CancellationToken.None);
        _entries.Clear();

        await store.SaveAsync(BuildWorkflow(id, "Mapped", ("n1", $",{longMapping}")), CancellationToken.None);

        var nodeRow = _entries.Single(x => x.EntityType == nameof(WorkflowNode));
        nodeRow.NewValueJson.Should().Contain("field199");
        nodeRow.NewValueJson.Should().NotContain("truncated");
    }

    [Fact]
    public async Task A_governance_failure_never_fails_the_save()
    {
        // The workflow is already committed by the time the audit row is written; the user's change succeeded.
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        var numbers = new Mock<IWorkflowNumberGenerator>();
        numbers.Setup(x => x.NextAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var governance = new Mock<IGovernanceLogger>();
        governance
            .Setup(g => g.LogAuditAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit table is gone"));

        await using var context = CreateContext();
        var store = new SqlWorkflowDefinitionStore(
            context, currentUser.Object, numbers.Object, logger: null, governanceLogger: governance.Object);

        var act = async () => await store.SaveAsync(
            BuildWorkflow(Guid.NewGuid(), "Resilient", ("n1", "")), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
