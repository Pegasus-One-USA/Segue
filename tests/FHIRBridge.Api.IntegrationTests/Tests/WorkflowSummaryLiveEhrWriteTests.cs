using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// The workflow list's "Writes to ..." badge comes from liveEhrWriteTargets on each summary row: the EHRs the
/// workflow's EHR Write-Back nodes write into for real. A test run or a dry run lists nothing.
/// </summary>
[Collection("ApiTests")]
public sealed class WorkflowSummaryLiveEhrWriteTests(ApiFixture f)
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

    private static Dictionary<string, string> WriteBack(string vendor, string dryRun, string? testAsVendor = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["dest_ehrVendor"] = vendor,
            ["dest_dryRun"] = dryRun,
            ["dest_resources"] = "AllergyIntolerance",
            ["dest_sourceConnectionId"] = Guid.NewGuid().ToString(),
        };
        if (testAsVendor is not null)
        {
            settings["dest_testAsVendor"] = testAsVendor;
        }

        return settings;
    }

    /// <summary>One source feeding each write-back node.</summary>
    private async Task CreateAsync(string name, params Dictionary<string, string>[] writeBacks)
    {
        var nodes = new List<object>();
        var edges = new List<object>();
        for (var i = 0; i < writeBacks.Length; i++)
        {
            nodes.Add(Node($"n-src-{i}", "EpicSourceNode", "Source", 0, new() { ["Resources"] = "AllergyIntolerance" }));
            nodes.Add(Node($"n-ewb-{i}", "EhrWriteBackDestinationNode", "Destination", 70, writeBacks[i]));
            edges.Add(new { FromNodeId = $"n-src-{i}", ToNodeId = $"n-ewb-{i}" });
        }

        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", new { Name = name, IsEnabled = false, Nodes = nodes, Edges = edges });
        await ApiFixture.EnsureOkAsync(resp);
    }

    private async Task<string[]> LiveEhrWriteTargetsOfAsync(string name)
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/workflows/summary?search={Uri.EscapeDataString(name)}&pageSize=50");
        await ApiFixture.EnsureOkAsync(resp);
        using var document = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = document.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);
        return row.GetProperty("liveEhrWriteTargets").EnumerateArray().Select(vendor => vendor.GetString()!).ToArray();
    }

    [Fact]
    public async Task A_live_write_back_lists_every_EHR_it_writes_into()
    {
        var name = $"Live write-back {Guid.NewGuid():N}";
        await CreateAsync(name, WriteBack("Epic", "false"), WriteBack("Athenahealth", "false"));

        Assert.Equal(["Epic", "Athenahealth"], await LiveEhrWriteTargetsOfAsync(name));
    }

    [Fact]
    public async Task A_test_run_or_a_dry_run_lists_nothing()
    {
        var name = $"Test and dry run write-back {Guid.NewGuid():N}";
        await CreateAsync(name, WriteBack("GenericFhir", "false", testAsVendor: "Healow"), WriteBack("Epic", "true"));

        Assert.Empty(await LiveEhrWriteTargetsOfAsync(name));
    }
}
