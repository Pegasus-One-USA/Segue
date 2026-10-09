using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// End-to-end verification of the permission pipeline: the access token carries no permission codes (they are
/// resolved per request by IUserPermissionsProvider, from role ∪ direct overrides), every request is checked by
/// <see cref="FHIRBridge.Api.Security.PermissionAuthorizationHandler"/>, a change to a user's permissions applies to
/// the session they already have, and a missing permission is rejected with a proper HTTP status rather than
/// silently allowed or a raw 500.
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

    // ── The token is an identity, not a permission list ──────────────────────────────

    [Fact]
    public async Task Access_token_carries_no_permission_codes()
    {
        // Permission codes used to be one claim each, which overflowed the 4 KB access-token cookie for a
        // SuperAdmin; they are now looked up per request from the token's "uid".
        var (userId, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        var token = new JwtSecurityToken(jwt);

        Assert.Empty(DecodePermissionClaims(jwt));
        Assert.Equal(userId.ToString(), token.Claims.Single(c => c.Type == "uid").Value);
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

    // ── Direct user-level overrides apply to the session the user already has ──

    [Fact]
    public async Task Granting_a_direct_override__applies_to_the_existing_session()
    {
        // The token is issued BEFORE the change and never re-issued. No request is made with it first: the
        // provider caches a user's permissions for up to 60 seconds (CachedUserPermissionsProvider), and a
        // cached set would hide the change until it expires.
        var (userId, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);
        var roleDeleteId = await f.GetPermissionIdByNameAsync("role.delete");

        var allocResp = await f.AdminClient.PutAsJsonAsync(
            $"/api/v1/users/{userId}/permission-allocations/{roleDeleteId}",
            new { IsEnabled = true });
        allocResp.EnsureSuccessStatusCode();

        // Permissions are resolved per request, so the same token now clears authorization: the
        // nonexistent-role probe reaches "not found".
        var resp = await client.DeleteAsync($"/api/v1/roles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Denying_a_role_granted_permission__removes_it_from_the_existing_session()
    {
        // As above: the token predates the change, and nothing caches its permissions before the change.
        var (userId, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);
        var userViewId = await f.GetPermissionIdByNameAsync("user.view");

        var allocResp = await f.AdminClient.PutAsJsonAsync(
            $"/api/v1/users/{userId}/permission-allocations/{userViewId}",
            new { IsEnabled = false });
        allocResp.EnsureSuccessStatusCode();

        var resp = await client.GetAsync($"/api/v1/users/{userId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
