using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// The Mapping Profiles master-screen endpoints added by docs/backend/14-mapping-profile-master-screen-plan.md:
/// paged list, get-by-id, activate/deactivate, and delete (with the route-usage guard — MappingProfileId is a
/// Restrict FK on ResourcePipelineRoute, so a profile referenced by a route must be rejected with 409 rather than
/// surfacing a raw DB error).
/// </summary>
[Collection("ApiTests")]
public sealed class MappingProfileMasterScreenTests(ApiFixture f)
{
    private static object Body(string name, string resourceType = "Patient") => new
    {
        Name = name,
        ResourceType = resourceType,
        SourceConnectionId = Guid.NewGuid(),
        DestinationId = Guid.NewGuid(),
        DestinationObject = "dbo.Patients",
        Fields = new[]
        {
            new
            {
                TargetField = "PatientId",
                JsonPath = "$.id",
                ValueType = "String",
                IsRequired = true,
                IsUpsertKey = true,
            },
        },
    };

    private async Task<Guid> CreateMappingProfileAsync(string name, string resourceType = "Patient")
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/mapping-profiles", Body(name, resourceType));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return doc.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Create_then_paged_list_finds_it_by_search()
    {
        var name = $"Patient-{Guid.NewGuid():N}";
        var id = await CreateMappingProfileAsync(name);

        var resp = await f.AdminClient.GetAsync($"/api/v1/mapping-profiles/paged?search={name}&page=1&pageSize=25");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, doc.GetProperty("totalCount").GetInt32());
        var items = doc.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(id, items[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Paged_list_filters_by_resourceType()
    {
        var name = $"Obs-{Guid.NewGuid():N}";
        await CreateMappingProfileAsync(name, "Observation");

        var wrongType = await f.AdminClient.GetAsync($"/api/v1/mapping-profiles/paged?search={name}&resourceType=Patient");
        wrongType.EnsureSuccessStatusCode();
        Assert.Equal(0, JsonDocument.Parse(await wrongType.Content.ReadAsStringAsync()).RootElement.GetProperty("totalCount").GetInt32());

        var rightType = await f.AdminClient.GetAsync($"/api/v1/mapping-profiles/paged?search={name}&resourceType=Observation");
        rightType.EnsureSuccessStatusCode();
        Assert.Equal(1, JsonDocument.Parse(await rightType.Content.ReadAsStringAsync()).RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Get_by_id_returns_the_created_profile_with_resolved_audit_fields()
    {
        var name = $"Patient-{Guid.NewGuid():N}";
        var id = await CreateMappingProfileAsync(name);

        var resp = await f.AdminClient.GetAsync($"/api/v1/mapping-profiles/{id}");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(name, doc.GetProperty("name").GetString());
        Assert.True(doc.TryGetProperty("createdOnUtc", out _));
    }

    [Fact]
    public async Task Get_by_id_returns_404_for_an_unknown_id()
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/mapping-profiles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Activate_then_deactivate_round_trips_isEnabled()
    {
        var id = await CreateMappingProfileAsync($"Patient-{Guid.NewGuid():N}");

        var deactivateResp = await f.AdminClient.PostAsync($"/api/v1/mapping-profiles/{id}/deactivate", null);
        deactivateResp.EnsureSuccessStatusCode();
        Assert.False(JsonDocument.Parse(await deactivateResp.Content.ReadAsStringAsync()).RootElement.GetProperty("isEnabled").GetBoolean());

        var activateResp = await f.AdminClient.PostAsync($"/api/v1/mapping-profiles/{id}/activate", null);
        activateResp.EnsureSuccessStatusCode();
        Assert.True(JsonDocument.Parse(await activateResp.Content.ReadAsStringAsync()).RootElement.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task Delete_an_unused_mapping_profile__removes_it_from_the_paged_list()
    {
        var name = $"ToDelete-{Guid.NewGuid():N}";
        var id = await CreateMappingProfileAsync(name);

        var deleteResp = await f.AdminClient.DeleteAsync($"/api/v1/mapping-profiles/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var listResp = await f.AdminClient.GetAsync($"/api/v1/mapping-profiles/paged?search={name}");
        listResp.EnsureSuccessStatusCode();
        Assert.Equal(0, JsonDocument.Parse(await listResp.Content.ReadAsStringAsync()).RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Delete_a_nonexistent_mapping_profile__returns_404()
    {
        var resp = await f.AdminClient.DeleteAsync($"/api/v1/mapping-profiles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_a_mapping_profile_used_by_a_route__returns_409()
    {
        var id = await CreateMappingProfileAsync($"InUse-{Guid.NewGuid():N}");

        var routeResp = await f.AdminClient.PostAsJsonAsync("/api/v1/resources", new
        {
            IsEnabled = true,
            IngestionMode = "ScheduledPull",
            WebhookConfigurationId = (Guid?)null,
            MappingProfileId = id,
            ScheduleExpression = "0 0 * * *",
            SearchParameters = (string?)null,
        });
        routeResp.EnsureSuccessStatusCode();

        var deleteResp = await f.AdminClient.DeleteAsync($"/api/v1/mapping-profiles/{id}");
        Assert.Equal(HttpStatusCode.Conflict, deleteResp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_client_cannot_list_paged_mapping_profiles()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/mapping-profiles/paged");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
