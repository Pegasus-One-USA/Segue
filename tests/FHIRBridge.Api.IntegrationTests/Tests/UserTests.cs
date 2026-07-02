using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class UserTests(ApiFixture f)
{
    // ── GET /api/v1/users ────────────────────────────────────────────────────────

    [Fact]
    public async Task GET_users__admin__returns_200_list()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        Assert.True(doc.GetArrayLength() > 0);
    }

    [Fact]
    public async Task GET_users__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── GET /api/v1/users/{userId} ───────────────────────────────────────────────

    [Fact]
    public async Task GET_users_by_id__existing_user__returns_200()
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/users/{f.AdminUserId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(f.AdminUserId, doc.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task GET_users_by_id__nonexistent_user__returns_404()
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_users_by_id__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.GetAsync($"/api/v1/users/{f.AdminUserId}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── POST /api/v1/users (create local user) ────────────────────────────────────

    [Fact]
    public async Task POST_users__valid_request__returns_201()
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email                 = $"newuser-{Guid.NewGuid():N}@testhospital.test",
            DisplayName           = "New User",
            Password              = "NewUser@Test123!",
            RoleNames             = new[] { "Operations" },
            RequirePasswordChange = false
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task POST_users__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email    = "anon@test.local",
            Password = "Test@123456!",
            RoleNames = new[] { "Operations" },
            RequirePasswordChange = false
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── PUT /api/v1/users/{userId} ───────────────────────────────────────────────

    [Fact]
    public async Task PUT_users_by_id__valid_request__returns_200()
    {
        var resp = await f.AdminClient.PutAsJsonAsync($"/api/v1/users/{f.InvitedUserId}", new
        {
            Email                 = "invited@testhospital.test",
            DisplayName           = "Updated Display Name",
            IsEnabled             = true,
            RoleNames             = new[] { "Operations" },
            NewPassword           = (string?)null,
            RequirePasswordChange = false
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── POST /api/v1/users/invite ────────────────────────────────────────────────

    [Fact]
    public async Task POST_users_invite__valid_request__returns_201_with_token()
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/users/invite", new
        {
            Email     = $"invite-{Guid.NewGuid():N}@testhospital.test",
            RoleId    = f.TestRoleId,
            FirstName = "Invitee",
            LastName  = "Test"
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("id", out _));
        // InvitationToken should be present for invited user
        Assert.True(doc.TryGetProperty("invitationToken", out _));
    }

    [Fact]
    public async Task POST_users_invite__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/users/invite", new
        {
            Email  = "anon-invite@test.local",
            RoleId = f.TestRoleId
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── POST /api/v1/users/accept-invite ─────────────────────────────────────────

    [Fact]
    public async Task POST_users_accept_invite__valid_token__returns_200()
    {
        // Create a fresh invite for this test so we have an unconsumed token
        var inviteResp = await f.AdminClient.PostAsJsonAsync("/api/v1/users/invite", new
        {
            Email     = $"accept-{Guid.NewGuid():N}@testhospital.test",
            RoleId    = f.TestRoleId,
            FirstName = "Accept",
            LastName  = "Test"
        });
        inviteResp.EnsureSuccessStatusCode();
        var inv = JsonDocument.Parse(await inviteResp.Content.ReadAsStringAsync()).RootElement;
        var invEmail = inv.GetProperty("email").GetString()!;
        var invToken = inv.GetProperty("invitationToken").GetString()!;

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/users/accept-invite", new
        {
            Email           = invEmail,
            InvitationToken = invToken,
            Password        = "Accepted@Pass123!",
            FirstName       = "Accepted",
            LastName        = "User"
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task POST_users_accept_invite__invalid_token__returns_400_or_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/users/accept-invite", new
        {
            Email           = "invited@testhospital.test",
            InvitationToken = "invalid-token-xyz",
            Password        = "Accepted@Pass123!",
            FirstName       = "Test",
            LastName        = "User"
        });
        Assert.True(
            resp.StatusCode == HttpStatusCode.BadRequest ||
            resp.StatusCode == HttpStatusCode.Unauthorized);
    }

    // ── PATCH /api/v1/users/{userId}/status ──────────────────────────────────────

    [Fact]
    public async Task PATCH_users_status__disable_user__returns_200()
    {
        var resp = await f.AdminClient.PatchAsJsonAsync(
            $"/api/v1/users/{f.InvitedUserId}/status",
            new { IsEnabled = false });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PATCH_users_status__enable_user__returns_200()
    {
        var resp = await f.AdminClient.PatchAsJsonAsync(
            $"/api/v1/users/{f.InvitedUserId}/status",
            new { IsEnabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PATCH_users_status__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PatchAsJsonAsync(
            $"/api/v1/users/{f.InvitedUserId}/status",
            new { IsEnabled = false });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── GET /api/v1/users/{userId}/roles ─────────────────────────────────────────

    [Fact]
    public async Task GET_users_roles__existing_user__returns_200_list()
    {
        var resp = await f.AdminClient.GetAsync($"/api/v1/users/{f.AdminUserId}/roles");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
    }

    // ── POST /api/v1/users/{userId}/roles ────────────────────────────────────────

    [Fact]
    public async Task POST_users_assign_role__valid_request__returns_200()
    {
        var resp = await f.AdminClient.PostAsJsonAsync(
            $"/api/v1/users/{f.InvitedUserId}/roles",
            new { RoleId = f.TestRoleId });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task POST_users_assign_role__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync(
            $"/api/v1/users/{f.InvitedUserId}/roles",
            new { RoleId = f.TestRoleId });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── DELETE /api/v1/users/{userId}/roles/{roleId} ──────────────────────────────

    [Fact]
    public async Task DELETE_users_role__existing_assignment__returns_204()
    {
        // First assign the role, then remove it
        await f.AdminClient.PostAsJsonAsync(
            $"/api/v1/users/{f.InvitedUserId}/roles",
            new { RoleId = f.TestRoleId });

        var resp = await f.AdminClient.DeleteAsync(
            $"/api/v1/users/{f.InvitedUserId}/roles/{f.TestRoleId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // ── DELETE /api/v1/users/{userId} ────────────────────────────────────────────

    [Fact]
    public async Task DELETE_users__existing_user__returns_204()
    {
        var resp = await f.AdminClient.DeleteAsync($"/api/v1/users/{f.UserToDeleteId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_users__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.DeleteAsync($"/api/v1/users/{f.UserToDeleteId}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
