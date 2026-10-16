using System.Net.Http.Json;
using System.Text.Json;
using FHIRBridge.Api.IntegrationTests.TestHelpers;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Per-resource-type search criteria are keyed by workflow id, so copying a workflow must carry them over — and under
/// the same source-node key, since the copy's nodes keep their stamped canvasNodeId.
/// </summary>
[Collection("ApiTests")]
public sealed class WorkflowCopyCriteriaTests(ApiFixture f)
{
    private static object Node(string id, string nodeType, string category, int rank, Dictionary<string, string> settings) => new
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
    };

    [Fact]
    public async Task Copying_a_workflow_copies_its_resource_type_criteria()
    {
        var created = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", new
        {
            Name = $"Criteria copy {Guid.NewGuid():N}",
            IsEnabled = false,
            Nodes = new[]
            {
                Node("n-src", "EpicSourceNode", "Source", 0, new() { ["Resources"] = "Patient" }),
                Node("n-dest", "SqlServerDestinationNode", "Destination", 70, new() { ["dest_resources"] = "Patient" }),
            },
            Edges = new[] { new { FromNodeId = "n-src", ToNodeId = "n-dest" } },
        });
        await ApiFixture.EnsureOkAsync(created);
        var originalId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await ApiFixture.EnsureOkAsync(await f.AdminClient.PostAsJsonAsync(
            $"/api/v1/workflows/{originalId}/resource-type-criteria",
            new { SourceNodeId = "n-src", ResourceType = "Patient", Criteria = "identifier=MRN-1", Replace = true }));

        var copied = await f.AdminClient.PostAsJsonAsync($"/api/v1/workflows/{originalId}/copy", new { Name = "Criteria copy (copy)" });
        await ApiFixture.EnsureOkAsync(copied);
        var copyId = JsonDocument.Parse(await copied.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var rows = await f.AdminClient.GetFromJsonAsync<JsonElement>($"/api/v1/workflows/{copyId}/resource-type-criteria");
        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal("n-src", row.GetProperty("sourceNodeId").GetString());
        Assert.Equal("Patient", row.GetProperty("resourceType").GetString());
        Assert.Equal("identifier=MRN-1", row.GetProperty("criteria").GetString());
    }
}
