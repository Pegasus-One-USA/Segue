using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// The incremental /workflows/{id}/nodes endpoints (Workflow V3 Step 2c): "Add to Workflow" must persist a node
/// against a workflow that may legitimately be mid-build (e.g. only a source, no destination yet) — unlike
/// /workflows/build, which validates and provisions a complete graph. See WorkflowNodeEndpoints.cs.
/// </summary>
[Collection("ApiTests")]
public sealed class WorkflowNodeEndpointsTests(ApiFixture f)
{
    private async Task<Guid> CreateEmptyWorkflowAsync(string name)
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", new
        {
            Name = name,
            IsEnabled = true,
            Nodes = Array.Empty<object>(),
            Edges = Array.Empty<object>(),
        });
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return doc.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Add_node_persists_it_against_a_workflow_with_no_destination_yet()
    {
        var workflowId = await CreateEmptyWorkflowAsync($"Wf-{Guid.NewGuid():N}");

        var resp = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/nodes", new
        {
            NodeType = "SampleSourceNode",
            Category = "Source",
            Rank = 0,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var nodeId = created.GetProperty("id").GetGuid();
        Assert.Equal(1, created.GetProperty("workflowVersion").GetInt32());

        var getResp = await f.AdminClient.GetAsync($"/api/v1/workflows/{workflowId}");
        getResp.EnsureSuccessStatusCode();
        var workflow = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        var nodes = workflow.GetProperty("nodes").EnumerateArray().ToArray();

        Assert.Single(nodes);
        Assert.Equal(nodeId, nodes[0].GetProperty("id").GetGuid());
        Assert.Equal("SampleSourceNode", nodes[0].GetProperty("nodeType").GetString());
    }

    [Fact]
    public async Task Add_node_rejects_an_unknown_node_type()
    {
        var workflowId = await CreateEmptyWorkflowAsync($"Wf-{Guid.NewGuid():N}");

        var resp = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/nodes", new
        {
            NodeType = "NotARealNodeType",
            Category = "Source",
            Rank = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Add_node_with_fromNodeId_also_creates_the_connecting_edge()
    {
        var workflowId = await CreateEmptyWorkflowAsync($"Wf-{Guid.NewGuid():N}");

        var sourceResp = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/nodes", new
        {
            NodeType = "SampleSourceNode",
            Category = "Source",
            Rank = 0,
        });
        var sourceNodeId = JsonDocument.Parse(await sourceResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var mappingResp = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/nodes", new
        {
            NodeType = "MappingNode",
            Category = "Transform",
            Rank = 10,
            FromNodeId = sourceNodeId,
        });
        mappingResp.EnsureSuccessStatusCode();
        var mappingNodeId = JsonDocument.Parse(await mappingResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var getResp = await f.AdminClient.GetAsync($"/api/v1/workflows/{workflowId}");
        var workflow = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        var edges = workflow.GetProperty("edges").EnumerateArray().ToArray();

        Assert.Single(edges);
        Assert.Equal(sourceNodeId, edges[0].GetProperty("fromNodeId").GetGuid());
        Assert.Equal(mappingNodeId, edges[0].GetProperty("toNodeId").GetGuid());
    }

    [Fact]
    public async Task Update_node_changes_only_the_requested_fields()
    {
        var workflowId = await CreateEmptyWorkflowAsync($"Wf-{Guid.NewGuid():N}");
        var addResp = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/nodes", new
        {
            NodeType = "SampleSourceNode",
            Category = "Source",
            Rank = 0,
            DisplayName = "Original",
            PositionX = 10,
            PositionY = 20,
        });
        var nodeId = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var updateResp = await f.AdminClient.PutAsJsonAsync($"/api/v1/workflows/{workflowId}/nodes/{nodeId}", new
        {
            DisplayName = "Renamed",
        });

        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = JsonDocument.Parse(await updateResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Renamed", updated.GetProperty("displayName").GetString());
        // PositionX/Y were omitted from the update request — must survive unchanged.
        Assert.Equal(10, updated.GetProperty("positionX").GetDouble());
        Assert.Equal(20, updated.GetProperty("positionY").GetDouble());
    }

    [Fact]
    public async Task Delete_node_removes_it_from_the_workflow()
    {
        var workflowId = await CreateEmptyWorkflowAsync($"Wf-{Guid.NewGuid():N}");
        var addResp = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/nodes", new
        {
            NodeType = "SampleSourceNode",
            Category = "Source",
            Rank = 0,
        });
        var nodeId = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var deleteResp = await f.AdminClient.DeleteAsync($"/api/v1/workflows/{workflowId}/nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var getResp = await f.AdminClient.GetAsync($"/api/v1/workflows/{workflowId}");
        var workflow = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Empty(workflow.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public async Task Add_node_to_a_missing_workflow_returns_not_found()
    {
        var resp = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{Guid.NewGuid()}/nodes", new
        {
            NodeType = "SampleSourceNode",
            Category = "Source",
            Rank = 0,
        });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
