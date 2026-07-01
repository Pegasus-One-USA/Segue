using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class RoleTests(ApiFixture f)
{
    // ── GET /api/v1/roles ────────────────────────────────────────────────────────

    [Fact]
    public async Task GET_roles__admin__returns_200_list()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/roles");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        Assert.True(doc.GetArrayLength() >= 5);   // at least the 5 seeded system roles
    }

    [Fact]
    public async Task GET_roles__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/roles");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── GET /api/v1/roles/{roleId} ───────────────────────────────────────────────

    [Fact]
    public async Task GET_roles_by_id__existing_role__returns_200()
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/roles/{f.TestRoleId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(f.TestRoleId, doc.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task GET_roles_by_id__nonexistent_role__returns_404()
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/roles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── POST /api/v1/roles ───────────────────────────────────────────────────────

    [Fact]
    public async Task POST_roles__valid_request__returns_201()
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/roles", new
        {
            Name          = $"NewRole-{Guid.NewGuid():N}",
            Description   = "A newly created role",
            PermissionIds = new[] { f.PermissionId }
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task POST_roles__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/roles", new
        {
            Name          = "AnonRole",
            Description   = "Should fail",
            PermissionIds = Array.Empty<Guid>()
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── PUT /api/v1/roles/{roleId} ───────────────────────────────────────────────

    [Fact]
    public async Task PUT_roles_by_id__valid_request__returns_200()
    {
        var resp = await f.AdminClient.PutAsJsonAsync($"/api/v1/roles/{f.TestRoleId}", new
        {
            Name          = "IntegrationTestRole-Updated",
            Description   = "Updated by integration test",
            PermissionIds = new[] { f.PermissionId }
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("IntegrationTestRole-Updated", doc.GetProperty("name").GetString());
    }

    [Fact]
    public async Task PUT_roles_by_id__nonexistent_role__returns_404()
    {
        var resp = await f.AdminClient.PutAsJsonAsync($"/api/v1/roles/{Guid.NewGuid()}", new
        {
            Name          = "Ghost Role",
            Description   = "Does not exist",
            PermissionIds = Array.Empty<Guid>()
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── GET /api/v1/roles/{roleId}/permissions ───────────────────────────────────

    [Fact]
    public async Task GET_role_permissions__existing_role__returns_200_list()
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/roles/{f.TestRoleId}/permissions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
    }

    // ── POST /api/v1/roles/{roleId}/permissions ──────────────────────────────────

    [Fact]
    public async Task POST_role_permissions__valid_request__returns_200()
    {
        var resp = await f.AdminClient.PostAsJsonAsync(
            $"/api/v1/roles/{f.TestRoleId}/permissions",
            new { PermissionIds = new[] { f.PermissionId } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task POST_role_permissions__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync(
            $"/api/v1/roles/{f.TestRoleId}/permissions",
            new { PermissionIds = new[] { f.PermissionId } });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── DELETE /api/v1/roles/{roleId}/permissions/{permissionId} ─────────────────

    [Fact]
    public async Task DELETE_role_permission__existing_permission__returns_204()
    {
        // Ensure the permission is assigned first
        await f.AdminClient.PostAsJsonAsync(
            $"/api/v1/roles/{f.TestRoleId}/permissions",
            new { PermissionIds = new[] { f.PermissionId } });

        var resp = await f.AdminClient.DeleteAsync(
            $"/api/v1/roles/{f.TestRoleId}/permissions/{f.PermissionId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // ── DELETE /api/v1/roles/{roleId} ────────────────────────────────────────────

    [Fact]
    public async Task DELETE_roles__custom_role_no_users__returns_204()
    {
        var resp = await f.AdminClient.DeleteAsync($"/api/v1/roles/{f.RoleToDeleteId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_roles__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.DeleteAsync($"/api/v1/roles/{f.RoleToDeleteId}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_roles__system_role__returns_400()
    {
        // Seeded system roles should be protected from deletion
        var rolesResp = await f.AdminClient.GetAsync("/api/v1/roles");
        var roles = JsonDocument.Parse(await rolesResp.Content.ReadAsStringAsync()).RootElement;
        var systemRole = roles.EnumerateArray()
            .FirstOrDefault(r => r.TryGetProperty("isSystemRole", out var sr) && sr.GetBoolean());

        if (systemRole.ValueKind == JsonValueKind.Undefined)
            return; // no system role found — skip

        var roleId = systemRole.GetProperty("id").GetGuid();
        var resp = await f.AdminClient.DeleteAsync($"/api/v1/roles/{roleId}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
