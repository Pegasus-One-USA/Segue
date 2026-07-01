using System.Net;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class PermissionTests(ApiFixture f)
{
    // ── GET /api/v1/permissions ──────────────────────────────────────────────────

    [Fact]
    public async Task GET_permissions__admin__returns_200_list_of_24()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        Assert.Equal(24, doc.GetArrayLength());
    }

    [Fact]
    public async Task GET_permissions__each_has_id_name_description()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        foreach (var perm in doc.EnumerateArray())
        {
            Assert.True(perm.TryGetProperty("id", out _),          "missing id");
            Assert.True(perm.TryGetProperty("name", out _),        "missing name");
            Assert.True(perm.TryGetProperty("description", out _), "missing description");
        }
    }

    [Fact]
    public async Task GET_permissions__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/permissions");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
