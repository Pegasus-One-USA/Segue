using System.Net;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class PermissionTests(ApiFixture f)
{
    // ── GET /api/v1/permissions ──────────────────────────────────────────────────

    [Fact]
    public async Task GET_permissions__admin__returns_200_list_of_20()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        // 20 seeded platform permissions (the 4 tenant.* permissions were removed with multi-tenancy).
        Assert.Equal(20, doc.GetArrayLength());
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

    // ── GET /api/v1/permissions/catalog ──────────────────────────────────────────

    [Fact]
    public async Task GET_permissions_catalog__admin__returns_200_with_2_categories()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions/catalog");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        // AccessControl + Platform — the 2 seeded top-level categories.
        Assert.Equal(2, doc.GetArrayLength());
    }

    [Fact]
    public async Task GET_permissions_catalog__categories_contain_groups_and_all_20_permissions()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions/catalog");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var totalPermissions = 0;
        foreach (var category in doc.EnumerateArray())
        {
            Assert.True(category.TryGetProperty("id", out _),          "category missing id");
            Assert.True(category.TryGetProperty("name", out _),        "category missing name");
            Assert.True(category.TryGetProperty("displayName", out _), "category missing displayName");
            Assert.True(category.TryGetProperty("groups", out var groups), "category missing groups");
            Assert.True(groups.GetArrayLength() > 0, "category has no groups");

            foreach (var group in groups.EnumerateArray())
            {
                Assert.True(group.TryGetProperty("id", out _),          "group missing id");
                Assert.True(group.TryGetProperty("displayName", out _), "group missing displayName");
                Assert.True(group.TryGetProperty("permissions", out var permissions), "group missing permissions");
                Assert.True(permissions.GetArrayLength() > 0, "group has no permissions");

                foreach (var permission in permissions.EnumerateArray())
                {
                    Assert.True(permission.TryGetProperty("id", out _),          "permission missing id");
                    Assert.True(permission.TryGetProperty("name", out _),        "permission missing name");
                    Assert.True(permission.TryGetProperty("displayName", out _), "permission missing displayName");
                    totalPermissions++;
                }
            }
        }

        Assert.Equal(20, totalPermissions);
    }

    [Fact]
    public async Task GET_permissions_catalog__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/permissions/catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
