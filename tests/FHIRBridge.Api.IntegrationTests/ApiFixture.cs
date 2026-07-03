using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests;

[CollectionDefinition("ApiTests")]
public sealed class ApiTestCollection : ICollectionFixture<ApiFixture> { }

/// <summary>
/// Shared fixture: authenticates as the auto-seeded single-org SuperAdmin once
/// (no tenant registration), then exposes pre-built state used by all test classes
/// in the "ApiTests" collection.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly ApiFactory _factory = new();

    // ── Shared clients ──────────────────────────────────────────────────────────
    public HttpClient AdminClient { get; private set; } = null!;
    public HttpClient AnonClient  { get; private set; } = null!;

    // ── Identity state ──────────────────────────────────────────────────────────
    public Guid   AdminUserId { get; private set; }
    public string AdminJwt    { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";

    // ── Role state ──────────────────────────────────────────────────────────────
    public Guid TestRoleId      { get; private set; }   // general-purpose role
    public Guid RoleToDeleteId  { get; private set; }   // used only by Delete test
    public Guid PermissionId    { get; private set; }   // first seeded permission

    // ── User state ──────────────────────────────────────────────────────────────
    public Guid   InvitedUserId   { get; private set; }
    public string InvitationToken { get; private set; } = "";
    public Guid   UserToDeleteId  { get; private set; }   // used only by Delete test
    public Guid   PwChangeUserId  { get; private set; }   // used only by ChangePassword test
    public string PwChangeUserEmail    { get; } = "pwchange@testhospital.test";
    public string PwChangeUserPassword { get; } = "PwChange@Test123!";

    // ── Token state ─────────────────────────────────────────────────────────────
    public string ResetToken { get; private set; } = "";

    // ── Constants ───────────────────────────────────────────────────────────────
    public const string AdminEmail    = "admin@testhospital.test";
    public const string AdminPassword = "Admin@Test1234!";

    public HttpClient CreateAuthenticatedClient(string jwt)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // ── Setup ───────────────────────────────────────────────────────────────────
    public async Task InitializeAsync()
    {
        AnonClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 1. First-run setup: no user is seeded, so create the sole SuperAdmin via the anonymous,
        //    one-shot setup endpoint. It creates the user (assigning the bootstrapped SuperAdmin role)
        //    and returns a signed-in session — the same response shape as internal/login.
        var loginResp = await AnonClient.PostAsJsonAsync("/api/v1/auth/setup-superadmin", new
        {
            Email       = AdminEmail,
            DisplayName = "Test Admin",
            Password    = AdminPassword
        });
        loginResp.EnsureSuccessStatusCode();

        var login = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync()).RootElement;
        AdminJwt    = login.GetProperty("accessToken").GetString()!;
        AdminUserId = login.GetProperty("profile").GetProperty("userId").GetGuid();
        if (login.TryGetProperty("refreshToken", out var rtProp) && rtProp.ValueKind != JsonValueKind.Null)
            RefreshToken = rtProp.GetString() ?? "";

        AdminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        AdminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminJwt);

        // 2. Get first permission ID (used for role creation tests).
        var permsResp = await AdminClient.GetAsync("/api/v1/permissions");
        permsResp.EnsureSuccessStatusCode();
        var perms = JsonDocument.Parse(await permsResp.Content.ReadAsStringAsync()).RootElement;
        PermissionId = perms[0].GetProperty("id").GetGuid();

        // 3. Create general-purpose test role.
        var crResp = await AdminClient.PostAsJsonAsync("/api/v1/roles", new
        {
            Name        = "IntegrationTestRole",
            Description = "Role created by integration test suite",
            PermissionIds = new[] { PermissionId }
        });
        crResp.EnsureSuccessStatusCode();
        TestRoleId = JsonDocument.Parse(await crResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 4. Create role specifically for DELETE test.
        var drResp = await AdminClient.PostAsJsonAsync("/api/v1/roles", new
        {
            Name        = "RoleToDelete",
            Description = "Will be deleted during integration tests",
            PermissionIds = Array.Empty<Guid>()
        });
        drResp.EnsureSuccessStatusCode();
        RoleToDeleteId = JsonDocument.Parse(await drResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 5. Invite a test user.
        var invResp = await AdminClient.PostAsJsonAsync("/api/v1/users/invite", new
        {
            Email     = "invited@testhospital.test",
            RoleId    = TestRoleId,
            FirstName = "Invited",
            LastName  = "User"
        });
        invResp.EnsureSuccessStatusCode();
        var inv = JsonDocument.Parse(await invResp.Content.ReadAsStringAsync()).RootElement;
        InvitedUserId = inv.GetProperty("id").GetGuid();
        if (inv.TryGetProperty("invitationToken", out var itProp) && itProp.ValueKind != JsonValueKind.Null)
            InvitationToken = itProp.GetString() ?? "";

        // 6. Create a user dedicated to the DELETE test.
        var duResp = await AdminClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email                 = "tobedeleted@testhospital.test",
            DisplayName           = "To Be Deleted",
            Password              = "Delete@Test123!",
            RoleNames             = new[] { "Operations" },
            RequirePasswordChange = false
        });
        duResp.EnsureSuccessStatusCode();
        UserToDeleteId = JsonDocument.Parse(await duResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 7. Create a user dedicated to the ChangePassword test.
        var pwResp = await AdminClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email                 = PwChangeUserEmail,
            DisplayName           = "PwChange User",
            Password              = PwChangeUserPassword,
            RoleNames             = new[] { "Operations" },
            RequirePasswordChange = false
        });
        pwResp.EnsureSuccessStatusCode();
        PwChangeUserId = JsonDocument.Parse(await pwResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 8. Trigger forgot-password to capture reset token (ExposeResetTokens=true in test config).
        var fpResp = await AnonClient.PostAsJsonAsync("/api/v1/auth/internal/forgot-password", new
        {
            Email = AdminEmail
        });
        if (fpResp.IsSuccessStatusCode)
        {
            var fp = JsonDocument.Parse(await fpResp.Content.ReadAsStringAsync()).RootElement;
            if (fp.TryGetProperty("resetToken", out var rstProp) && rstProp.ValueKind != JsonValueKind.Null)
                ResetToken = rstProp.GetString() ?? "";
        }
    }

    public async Task DisposeAsync()
    {
        AdminClient?.Dispose();
        AnonClient?.Dispose();
        await _factory.DisposeAsync();
    }
}
