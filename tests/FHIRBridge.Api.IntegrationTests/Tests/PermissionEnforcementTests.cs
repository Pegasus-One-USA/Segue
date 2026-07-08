using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// End-to-end verification of the permission pipeline: login issues a JWT carrying exactly the
/// user's effective (role ∪ direct-override) permissions, every request re-checks those claims via
/// <see cref="FHIRBridge.Api.Security.PermissionAuthorizationHandler"/>, and a missing permission is
/// rejected with a proper HTTP status rather than silently allowed or a raw 500.
/// </summary>
[Collection("ApiTests")]
public sealed class PermissionEnforcementTests(ApiFixture f)
{
    private static IReadOnlyCollection<string> DecodePermissionClaims(string jwt)
    {
        var token = new JwtSecurityToken(jwt);
        return token.Claims
            .Where(c => c.Type == "permissions")
            .Select(c => c.Value)
            .ToArray();
    }

    // ── Login token carries the right permissions ──────────────────────────────

    [Fact]
    public async Task Login_token_carries_exactly_the_roles_granted_permissions()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        var claims = DecodePermissionClaims(jwt);

        Assert.Contains("user.view", claims, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("role.delete", claims, StringComparer.OrdinalIgnoreCase);
    }

    // ── Endpoint access follows the token's permissions ─────────────────────────

    [Fact]
    public async Task Endpoint_requiring_a_granted_permission__returns_200()
    {
        var (userId, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync($"/api/v1/users/{userId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Endpoint_requiring_an_ungranted_permission__returns_403()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        // Authorization is checked before the action runs, so a nonexistent role id still 403s —
        // if the permission check were bypassed this would 404 instead.
        var resp = await client.DeleteAsync($"/api/v1/roles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_call_to_a_permission_gated_endpoint__returns_401_not_403()
    {
        var resp = await f.AnonClient.DeleteAsync($"/api/v1/roles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Direct user-level overrides change the next login's token ──────────────

    [Fact]
    public async Task Granting_a_direct_override__adds_the_permission_on_next_login_only()
    {
        var (userId, email, staleJwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        var roleDeleteId = await f.GetPermissionIdByNameAsync("role.delete");

        var allocResp = await f.AdminClient.PutAsJsonAsync(
            $"/api/v1/users/{userId}/permission-allocations/{roleDeleteId}",
            new { IsEnabled = true });
        allocResp.EnsureSuccessStatusCode();

        // The access token already issued is unaffected — an accepted JWT tradeoff (the override
        // takes effect once the user's refresh token is invalidated and they log in again).
        Assert.DoesNotContain("role.delete", DecodePermissionClaims(staleJwt), StringComparer.OrdinalIgnoreCase);
        using var staleClient = f.CreateAuthenticatedClient(staleJwt);
        var staleResp = await staleClient.DeleteAsync($"/api/v1/roles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, staleResp.StatusCode);

        // A fresh login picks up the override — same nonexistent-role probe now clears
        // authorization and reaches the "not found" business logic instead.
        var freshJwt = await f.LoginAsync(email);
        Assert.Contains("role.delete", DecodePermissionClaims(freshJwt), StringComparer.OrdinalIgnoreCase);
        using var freshClient = f.CreateAuthenticatedClient(freshJwt);
        var freshResp = await freshClient.DeleteAsync($"/api/v1/roles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, freshResp.StatusCode);
    }

    [Fact]
    public async Task Denying_a_role_granted_permission__removes_it_on_next_login_only()
    {
        var (userId, email, _) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        var userViewId = await f.GetPermissionIdByNameAsync("user.view");

        var allocResp = await f.AdminClient.PutAsJsonAsync(
            $"/api/v1/users/{userId}/permission-allocations/{userViewId}",
            new { IsEnabled = false });
        allocResp.EnsureSuccessStatusCode();

        var freshJwt = await f.LoginAsync(email);
        Assert.DoesNotContain("user.view", DecodePermissionClaims(freshJwt), StringComparer.OrdinalIgnoreCase);

        using var client = f.CreateAuthenticatedClient(freshJwt);
        var resp = await client.GetAsync($"/api/v1/users/{userId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
