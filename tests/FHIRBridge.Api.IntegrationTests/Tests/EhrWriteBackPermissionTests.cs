using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FHIRBridge.Api.IntegrationTests.TestHelpers;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// With no installation-wide live-write release, the EHR Write-Back permissions are the only gate on writing into an
/// EHR. An EHR Write-Back node runs from its own settings (dest_dryRun and the rest), so every workflow route that
/// stores, copies, arms or runs one must check those permissions itself. Each test here takes one route that used to
/// let a caller around them and checks it is now refused.
/// </summary>
[Collection("ApiTests")]
public sealed class EhrWriteBackPermissionTests(ApiFixture f)
{
    private const string WriteBackNodeId = "n-ewb";

    /// <summary>Fixed, so a resave with the same settings really is unchanged.</summary>
    private static readonly string TargetConnectionId = Guid.NewGuid().ToString();

    private static object Node(
        string id, string nodeType, string category, int rank, Dictionary<string, string> settings, bool checkpointUrlEnabled = false) => new
    {
        Id = id,
        NodeType = nodeType,
        Category = category,
        Rank = rank,
        SubRank = 0,
        DisplayName = nodeType,
        ConfigurationJson = JsonSerializer.Serialize(settings),
        PositionX = 0,
        PositionY = 0,
        IsEnabled = true,
        CheckpointUrlEnabled = checkpointUrlEnabled,
    };

    /// <summary>A source feeding one EHR Write-Back node. It has no destinationId: the node runs from its own
    /// settings, which is exactly why the checks must not depend on one.</summary>
    private static object Workflow(
        bool dryRun, bool enabled = false, object? trigger = null, bool publiclyLaunchable = false, bool checkpoint = false) => new
    {
        Name = $"EHR write-back RBAC {Guid.NewGuid():N}",
        IsEnabled = enabled,
        IsPubliclyLaunchable = publiclyLaunchable,
        Trigger = trigger,
        Nodes = new[]
        {
            Node("n-src", "EpicSourceNode", "Source", 0, new() { ["Resources"] = "AllergyIntolerance" }),
            Node(WriteBackNodeId, "EhrWriteBackDestinationNode", "Destination", 70, new()
            {
                ["dest_dryRun"] = dryRun ? "true" : "false",
                ["dest_resources"] = "AllergyIntolerance",
                ["dest_sourceConnectionId"] = TargetConnectionId,
            }, checkpointUrlEnabled: checkpoint),
        },
        Edges = new[] { new { FromNodeId = "n-src", ToNodeId = WriteBackNodeId } },
    };

    private static readonly object Schedule = new { Type = "Schedule", ScheduleExpression = "0 * * * *" };

    private async Task<Guid> CreateAsAdminAsync(object workflow)
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", workflow);
        await ApiFixture.EnsureOkAsync(resp);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> ClientWithAsync(params string[] permissions)
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync(permissions);
        return f.CreateAuthenticatedClient(jwt);
    }

    [Fact]
    public async Task Workflow_edit_alone_cannot_take_a_write_back_node_off_dry_run()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: true));
        using var editor = await ClientWithAsync("workflow.view", "workflow.edit");

        var resp = await editor.PutAsJsonAsync($"/api/v1/workflows/{id}", Workflow(dryRun: false));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Ehrwriteback_edit_may_take_a_write_back_node_off_dry_run()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: true));
        using var editor = await ClientWithAsync("workflow.view", "workflow.edit", "ehrwriteback.edit");

        var resp = await editor.PutAsJsonAsync($"/api/v1/workflows/{id}", Workflow(dryRun: false));

        await ApiFixture.EnsureOkAsync(resp);
    }

    [Fact]
    public async Task Workflow_edit_may_resave_a_write_back_workflow_without_changing_its_write_settings()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: true));
        using var editor = await ClientWithAsync("workflow.view", "workflow.edit");

        var resp = await editor.PutAsJsonAsync($"/api/v1/workflows/{id}", Workflow(dryRun: true));

        await ApiFixture.EnsureOkAsync(resp);
    }

    [Fact]
    public async Task Workflow_create_alone_cannot_add_a_write_back_node()
    {
        using var creator = await ClientWithAsync("workflow.view", "workflow.create");

        var resp = await creator.PostAsJsonAsync("/api/v1/workflows", Workflow(dryRun: false));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [DatabaseFact]
    public async Task Run_needs_ehrwriteback_execute_even_when_the_node_has_no_destination_id()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: false));
        using var runner = await ClientWithAsync("workflow.view", "workflow.run");

        var resp = await runner.PostAsJsonAsync($"/api/v1/workflows/{id}/run", new { });

        Assert.True(resp.StatusCode == HttpStatusCode.Forbidden, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    [DatabaseFact]
    public async Task An_anonymous_caller_cannot_run_a_publicly_launchable_write_back_workflow()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: false, publiclyLaunchable: true));

        var resp = await f.AnonClient.PostAsJsonAsync($"/api/v1/workflows/{id}/run", new { });

        Assert.True(resp.StatusCode == HttpStatusCode.NotFound, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_checkpoint_url_is_never_minted_for_a_write_back_node()
    {
        var created = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", Workflow(dryRun: false, checkpoint: true));
        await ApiFixture.EnsureOkAsync(created);
        var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var id = doc.GetProperty("id").GetGuid();
        var nodeId = doc.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("nodeType").GetString() == "EhrWriteBackDestinationNode")
            .GetProperty("id").GetGuid();

        var resp = await f.AdminClient.GetAsync($"/api/v1/workflows/{id}/nodes/{nodeId}/checkpoint-url");

        // The checkpoint flag is on, so this is refused for being a write-back node, not for a missing flag.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("checkpoint_not_allowed", await resp.Content.ReadAsStringAsync());
        Assert.DoesNotContain("checkpointUrl", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Workflow_edit_alone_cannot_arm_a_scheduled_write_back_workflow()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: false, enabled: false, trigger: Schedule));
        using var editor = await ClientWithAsync("workflow.view", "workflow.edit");

        var resp = await editor.PostAsync($"/api/v1/workflows/{id}/activate", null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Run_and_execute_rights_may_arm_a_scheduled_write_back_workflow()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: false, enabled: false, trigger: Schedule));
        using var operatorClient = await ClientWithAsync("workflow.view", "workflow.edit", "workflow.run", "ehrwriteback.execute");

        var resp = await operatorClient.PostAsync($"/api/v1/workflows/{id}/activate", null);

        await ApiFixture.EnsureOkAsync(resp);
        // Leave nothing armed for the rest of the suite.
        await ApiFixture.EnsureOkAsync(await f.AdminClient.PostAsync($"/api/v1/workflows/{id}/deactivate", null));
    }

    [Fact]
    public async Task Copying_a_write_back_workflow_needs_ehrwriteback_create()
    {
        var id = await CreateAsAdminAsync(Workflow(dryRun: false));
        using var creator = await ClientWithAsync("workflow.view", "workflow.create");

        var resp = await creator.PostAsJsonAsync($"/api/v1/workflows/{id}/copy", new { Name = "Copy of write-back" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
