using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class AuthTests(ApiFixture f)
{
    // ── GET /api/v1/auth/me ─────────────────────────────────────────────────────

    [Fact]
    public async Task GET_auth_me__authenticated__returns_200()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("externalUserId", body);
    }

    [Fact]
    public async Task GET_auth_me__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── POST /api/v1/auth/login (Entra record) ──────────────────────────────────

    [Fact]
    public async Task POST_auth_login__authenticated__returns_200()
    {
        var resp = await f.AdminClient.PostAsync("/api/v1/auth/login", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_login__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsync("/api/v1/auth/login", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── POST /api/v1/auth/internal/login ────────────────────────────────────────

    [Fact]
    public async Task POST_auth_internal_login__valid_credentials__returns_200_with_token()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new
        {
            Email    = ApiFixture.AdminEmail,
            Password = ApiFixture.AdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("accessToken", out _));
        Assert.True(doc.TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task POST_auth_internal_login__wrong_password__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new
        {
            Email    = ApiFixture.AdminEmail,
            Password = "WrongPassword999!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_internal_login__unknown_email__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new
        {
            Email    = "nobody@nowhere.test",
            Password = "SomePassword123!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── POST /api/v1/auth/internal/change-password ──────────────────────────────

    [Fact]
    public async Task POST_auth_internal_change_password__valid_request__returns_200()
    {
        // Login as dedicated pw-change user to avoid affecting shared admin token
        var loginResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new
        {
            Email    = f.PwChangeUserEmail,
            Password = f.PwChangeUserPassword
        });
        loginResp.EnsureSuccessStatusCode();
        var jwt = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

        using var client = f.CreateAuthenticatedClient(jwt);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/internal/change-password", new
        {
            CurrentPassword = f.PwChangeUserPassword,
            NewPassword     = "PwChange@New123!"
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_internal_change_password__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/change-password", new
        {
            CurrentPassword = "anything",
            NewPassword     = "anything123!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── POST /api/v1/auth/internal/forgot-password ──────────────────────────────

    [Fact]
    public async Task POST_auth_internal_forgot_password__known_email__returns_202()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/forgot-password", new
        {
            Email = ApiFixture.AdminEmail
        });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_internal_forgot_password__unknown_email__returns_202_no_enumeration()
    {
        // HIPAA: should never reveal whether email exists
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/forgot-password", new
        {
            Email = "does-not-exist@nowhere.test"
        });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    // ── POST /api/v1/auth/internal/reset-password ───────────────────────────────

    [Fact]
    public async Task POST_auth_internal_reset_password__valid_token__returns_204()
    {
        // Generate a fresh reset token so we aren't affected by other tests that also
        // call forgot-password for AdminEmail and overwrite the shared fixture token.
        var forgotResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/forgot-password", new
        {
            Email = ApiFixture.AdminEmail
        });
        forgotResp.EnsureSuccessStatusCode();
        var fp = System.Text.Json.JsonDocument.Parse(await forgotResp.Content.ReadAsStringAsync()).RootElement;
        if (!fp.TryGetProperty("resetToken", out var tokProp) || tokProp.ValueKind == System.Text.Json.JsonValueKind.Null)
            return; // token not exposed — skip (ExposeResetTokens must be false in this environment)

        var freshResetToken = tokProp.GetString()!;

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/reset-password", new
        {
            Email       = ApiFixture.AdminEmail,
            ResetToken  = freshResetToken,
            NewPassword = ApiFixture.AdminPassword  // reset back to same value
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_internal_reset_password__invalid_token__returns_400_or_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/reset-password", new
        {
            Email       = ApiFixture.AdminEmail,
            ResetToken  = "totally-invalid-token",
            NewPassword = "Anything@123!"
        });
        Assert.True(
            resp.StatusCode == HttpStatusCode.BadRequest ||
            resp.StatusCode == HttpStatusCode.Unauthorized);
    }

    // ── POST /api/v1/auth/refresh ────────────────────────────────────────────────

    [Fact]
    public async Task POST_auth_refresh__valid_token__returns_200_with_new_token()
    {
        // Get a fresh refresh token so we don't exhaust the fixture's shared one
        var loginResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new
        {
            Email    = ApiFixture.AdminEmail,
            Password = ApiFixture.AdminPassword
        });
        loginResp.EnsureSuccessStatusCode();
        var freshRefreshToken = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("refreshToken").GetString()!;

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = freshRefreshToken
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task POST_auth_refresh__invalid_token__returns_401_or_400()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = "invalid-refresh-token-abc123"
        });
        Assert.True(
            resp.StatusCode == HttpStatusCode.Unauthorized ||
            resp.StatusCode == HttpStatusCode.BadRequest);
    }

    // ── POST /api/v1/auth/logout ─────────────────────────────────────────────────

    [Fact]
    public async Task POST_auth_logout__authenticated__returns_204()
    {
        // Login fresh so we don't invalidate the shared admin client's session
        var loginResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new
        {
            Email    = ApiFixture.AdminEmail,
            Password = ApiFixture.AdminPassword
        });
        loginResp.EnsureSuccessStatusCode();
        var jwt = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

        using var client = f.CreateAuthenticatedClient(jwt);
        var resp = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_logout__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
