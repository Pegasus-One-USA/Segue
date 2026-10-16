using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

// Runs stopped reading a source's own Search criteria (node field and connection value); this one-time migration moves
// them into per-resource-type criteria rows so existing workflows keep filtering exactly as before.
public sealed class ResourceTypeCriteriaBackfillTests
{
    private static SourceAuthenticationConfiguration Auth => new(AuthenticationType.None, null, null, [], null, null, null);

    [Fact]
    public async Task A_node_search_criteria_becomes_a_patient_row_keyed_by_a_newly_stamped_canvas_id()
    {
        var database = SharedInMemoryDatabase.NewDatabaseName();
        var (workflowId, nodeId) = await SeedWorkflowAsync(database, sourceNodeConfig: """{"Search criteria":"identifier=A,B"}""", destResources: "Patient,Observation");

        await RunAsync(database);

        await using var context = Context(database);
        var row = await context.ResourceTypeCriteria.SingleAsync(r => r.WorkflowId == workflowId);
        row.ResourceType.Should().Be("Patient");
        row.Criteria.Should().Be("identifier=A,B");
        row.SourceNodeId.Should().Be(nodeId.ToString());
        Settings(await context.WorkflowNodes.SingleAsync(n => n.Id == nodeId)).GetProperty("canvasNodeId").GetString()
            .Should().Be(nodeId.ToString());
    }

    [Fact]
    public async Task The_connection_criteria_fill_parameters_the_node_did_not_set_and_win_over_an_existing_row()
    {
        var database = SharedInMemoryDatabase.NewDatabaseName();
        var (workflowId, _) = await SeedWorkflowAsync(
            database,
            sourceNodeConfig: """{"canvasNodeId":"n-1","Search criteria":"identifier=NODE"}""",
            destResources: "Patient",
            connectionCriteria: "identifier=CONN&active=true",
            existingRow: ("Patient", "identifier=ROW&gender=male"));

        await RunAsync(database);

        await using var context = Context(database);
        // Old run-time precedence: node over connection, and source criteria over the resource row.
        (await context.ResourceTypeCriteria.SingleAsync(r => r.WorkflowId == workflowId)).Criteria
            .Should().Be("identifier=NODE&active=true&gender=male");
    }

    [Fact]
    public async Task Without_patient_the_criteria_go_to_each_patient_compartment_type_the_workflow_fetches()
    {
        var database = SharedInMemoryDatabase.NewDatabaseName();
        var (workflowId, _) = await SeedWorkflowAsync(database, sourceNodeConfig: """{"canvasNodeId":"n-1","Search criteria":"category=laboratory"}""", destResources: "Observation,Location");

        await RunAsync(database);

        await using var context = Context(database);
        (await context.ResourceTypeCriteria.Where(r => r.WorkflowId == workflowId).Select(r => r.ResourceType).ToListAsync())
            .Should().Equal("Observation");
    }

    [Fact]
    public async Task A_workflow_copied_before_this_change_inherits_its_originals_rows()
    {
        var database = SharedInMemoryDatabase.NewDatabaseName();
        var originalId = Guid.NewGuid();
        var (copyId, _) = await SeedWorkflowAsync(
            database,
            sourceNodeConfig: $$"""{"canvasNodeId":"n-1","workflowId":"{{originalId}}"}""",
            destResources: "Patient",
            extraRows: [new ResourceTypeCriteria(originalId, "n-1", "Patient", "identifier=ORIG")]);

        await RunAsync(database);

        await using var context = Context(database);
        (await context.ResourceTypeCriteria.SingleAsync(r => r.WorkflowId == copyId)).Criteria.Should().Be("identifier=ORIG");
    }

    [Fact]
    public async Task It_runs_only_once()
    {
        var database = SharedInMemoryDatabase.NewDatabaseName();
        var (workflowId, _) = await SeedWorkflowAsync(database, sourceNodeConfig: """{"canvasNodeId":"n-1","Search criteria":"identifier=A"}""", destResources: "Patient");
        await RunAsync(database);

        // A user later clears the Patient criteria on purpose; a second start must not put the old value back.
        await using (var edit = Context(database))
        {
            var row = await edit.ResourceTypeCriteria.SingleAsync(r => r.WorkflowId == workflowId);
            edit.ResourceTypeCriteria.Remove(row);
            await edit.SaveChangesAsync();
        }

        await RunAsync(database);

        await using var context = Context(database);
        (await context.ResourceTypeCriteria.AnyAsync(r => r.WorkflowId == workflowId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_workflow_without_old_criteria_is_left_alone()
    {
        var database = SharedInMemoryDatabase.NewDatabaseName();
        var (workflowId, nodeId) = await SeedWorkflowAsync(database, sourceNodeConfig: """{"Resources":"Patient"}""", destResources: "Patient");

        await RunAsync(database);

        await using var context = Context(database);
        (await context.ResourceTypeCriteria.AnyAsync(r => r.WorkflowId == workflowId)).Should().BeFalse();
        Settings(await context.WorkflowNodes.SingleAsync(n => n.Id == nodeId)).TryGetProperty("canvasNodeId", out _).Should().BeFalse();
    }

    // With the auditing interceptor, as in the app: it fills the required CreatedBy on the rows the migration inserts.
    private static FHIRBridgeDbContext Context(string database)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("system", "system@example.com", "System", [], true));
        return new FHIRBridgeDbContext(
            SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(database, new AuditingSaveChangesInterceptor(currentUser.Object)));
    }

    private static async Task RunAsync(string database)
    {
        await using var context = Context(database);
        await new ResourceTypeCriteriaBackfill(context, NullLogger<ResourceTypeCriteriaBackfill>.Instance).EnsureAsync(CancellationToken.None);
    }

    private static JsonElement Settings(WorkflowNode node) => JsonDocument.Parse(node.ConfigurationJson).RootElement;

    private static async Task<(Guid WorkflowId, Guid SourceNodeId)> SeedWorkflowAsync(
        string database,
        string sourceNodeConfig,
        string destResources,
        string? connectionCriteria = null,
        (string ResourceType, string Criteria)? existingRow = null,
        IReadOnlyList<ResourceTypeCriteria>? extraRows = null)
    {
        await using var context = Context(database);
        var now = DateTime.UtcNow;
        var connection = new SourceConnection(
            "Epic", SourceSystemType.Epic, "https://fhir.example.org", Auth,
            retrieval: new SourceRetrievalConfiguration("search-rest", ["Patient"], connectionCriteria, false));
        connection.ApplyCreated("test@example.com", now);
        context.SourceConnections.Add(connection);

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "Backfill test", 1);
        var nodeSettings = System.Text.Json.Nodes.JsonNode.Parse(sourceNodeConfig)!.AsObject();
        nodeSettings["sourceConnectionId"] = connection.Id.ToString();
        var withConnection = nodeSettings.ToJsonString();
        var source = workflow.AddNode("EpicSourceNode", WorkflowNodeCategory.Source, 0, configurationJson: withConnection);
        var destination = workflow.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}"}""");
        workflow.AddEdge(source.Id, destination.Id);
        workflow.StampAudit(now, "test@example.com", null, null);
        context.WorkflowDefinitions.Add(workflow);

        if (existingRow is { } row)
        {
            var canvasNodeId = JsonDocument.Parse(sourceNodeConfig).RootElement.GetProperty("canvasNodeId").GetString()!;
            context.ResourceTypeCriteria.Add(new ResourceTypeCriteria(workflow.Id, canvasNodeId, row.ResourceType, row.Criteria));
        }

        foreach (var extra in extraRows ?? [])
        {
            context.ResourceTypeCriteria.Add(extra);
        }

        await context.SaveChangesAsync();
        return (workflow.Id, source.Id);
    }
}
