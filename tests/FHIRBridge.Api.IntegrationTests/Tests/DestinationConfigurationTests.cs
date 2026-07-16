using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// CRUD + paging for /api/v1/destinations. The execution-history gate (edit/delete blocked once a destination has
/// pipeline run history) is covered separately in FHIRBridge.UnitTests.Configuration.DestinationExecutionHistoryGateTests
/// against a mocked repository — this test host's InMemoryConfigurationRepository never reports execution history
/// (there is no in-memory store of pipeline runs), so only the "no history" paths are exercisable here.
/// </summary>
[Collection("ApiTests")]
public sealed class DestinationConfigurationTests(ApiFixture f)
{
    private static object CreateBody(string name, string target = "dbo.Patients") => new
    {
        Name           = name,
        DestinationType = "SqlServer",
        KeyVaultName   = "test-vault",
        SecretName     = $"secret-{Guid.NewGuid():N}",
        Target         = target
    };

    private async Task<Guid> CreateDestinationAsync(string name)
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/destinations", CreateBody(name));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return doc.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Create_then_paged_list_finds_it_by_search()
    {
        var name = $"Warehouse-{Guid.NewGuid():N}";
        var id = await CreateDestinationAsync(name);

        var resp = await f.AdminClient.GetAsync($"/api/v1/destinations/paged?search={name}&page=1&pageSize=25");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, doc.GetProperty("totalCount").GetInt32());
        var items = doc.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(id, items[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Paged_list_respects_pageSize()
    {
        var prefix = $"Batch-{Guid.NewGuid():N}";
        for (var i = 0; i < 3; i++)
        {
            await CreateDestinationAsync($"{prefix}-{i}");
        }

        var resp = await f.AdminClient.GetAsync($"/api/v1/destinations/paged?search={prefix}&page=1&pageSize=2");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(3, doc.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, doc.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Paged_list_filters_by_destinationType()
    {
        var name = $"CsvOnly-{Guid.NewGuid():N}";
        var createResp = await f.AdminClient.PostAsJsonAsync("/api/v1/destinations", new
        {
            Name            = name,
            DestinationType = "Csv",
            KeyVaultName    = "test-vault",
            SecretName      = $"secret-{Guid.NewGuid():N}",
            Target          = (string?)null
        });
        createResp.EnsureSuccessStatusCode();

        var wrongTypeResp = await f.AdminClient.GetAsync($"/api/v1/destinations/paged?search={name}&destinationType=SqlServer");
        wrongTypeResp.EnsureSuccessStatusCode();
        Assert.Equal(0, JsonDocument.Parse(await wrongTypeResp.Content.ReadAsStringAsync()).RootElement.GetProperty("totalCount").GetInt32());

        var rightTypeResp = await f.AdminClient.GetAsync($"/api/v1/destinations/paged?search={name}&destinationType=Csv");
        rightTypeResp.EnsureSuccessStatusCode();
        Assert.Equal(1, JsonDocument.Parse(await rightTypeResp.Content.ReadAsStringAsync()).RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Has_execution_history__reports_false_for_a_fresh_destination()
    {
        var id = await CreateDestinationAsync($"Fresh-{Guid.NewGuid():N}");

        var resp = await f.AdminClient.GetAsync($"/api/v1/destinations/{id}/has-execution-history");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        Assert.False(doc.GetProperty("hasExecutionHistory").GetBoolean());
    }

    [Fact]
    public async Task Update_a_destination_with_no_history__succeeds()
    {
        var id = await CreateDestinationAsync($"ToRename-{Guid.NewGuid():N}");
        var newName = $"Renamed-{Guid.NewGuid():N}";

        var resp = await f.AdminClient.PutAsJsonAsync($"/api/v1/destinations/{id}", CreateBody(newName));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(newName, doc.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_a_destination_with_no_history__removes_it_from_the_paged_list()
    {
        var name = $"ToDelete-{Guid.NewGuid():N}";
        var id = await CreateDestinationAsync(name);

        var deleteResp = await f.AdminClient.DeleteAsync($"/api/v1/destinations/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var listResp = await f.AdminClient.GetAsync($"/api/v1/destinations/paged?search={name}");
        listResp.EnsureSuccessStatusCode();
        Assert.Equal(0, JsonDocument.Parse(await listResp.Content.ReadAsStringAsync()).RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Delete_a_nonexistent_destination__returns_404()
    {
        var resp = await f.AdminClient.DeleteAsync($"/api/v1/destinations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_client_cannot_list_paged_destinations()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/destinations/paged");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
