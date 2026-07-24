using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class AllowedCorsOriginsTests(ApiFixture f)
{
    // ── GET /api/v1/system/allowed-origins ───────────────────────────────────────

    [Fact]
    public async Task GET_allowed_origins__superadmin__returns_200_list()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/system/allowed-origins");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
    }

    [Fact]
    public async Task GET_allowed_origins__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/system/allowed-origins");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GET_allowed_origins__non_superadmin__returns_403()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("configuration.write");
        var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/system/allowed-origins");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── POST /api/v1/system/allowed-origins ──────────────────────────────────────

    [Fact]
    public async Task POST_allowed_origins__valid_https_origin__returns_201()
    {
        var origin = $"https://{Guid.NewGuid():N}.example.com";
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/system/allowed-origins", new
        {
            OriginUrl = origin,
            Label     = "Integration test"
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(origin, doc.GetProperty("originUrl").GetString());
    }

    [Fact]
    public async Task POST_allowed_origins__url_with_path__returns_400()
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/system/allowed-origins", new
        {
            OriginUrl = "https://portal.example.com/some-path",
            Label     = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_allowed_origins__duplicate_origin__returns_400()
    {
        var origin = $"https://{Guid.NewGuid():N}.example.com";
        await f.AdminClient.PostAsJsonAsync("/api/v1/system/allowed-origins", new { OriginUrl = origin, Label = (string?)null });

        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/system/allowed-origins", new { OriginUrl = origin, Label = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_allowed_origins__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/system/allowed-origins", new
        {
            OriginUrl = "https://should-fail.example.com",
            Label     = (string?)null
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task POST_allowed_origins__non_superadmin__returns_403()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("configuration.write");
        var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsJsonAsync("/api/v1/system/allowed-origins", new
        {
            OriginUrl = "https://should-also-fail.example.com",
            Label     = (string?)null
        });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── DELETE /api/v1/system/allowed-origins/{id} ───────────────────────────────

    [Fact]
    public async Task DELETE_allowed_origins__existing_origin__returns_204()
    {
        var origin = $"https://{Guid.NewGuid():N}.example.com";
        var createResp = await f.AdminClient.PostAsJsonAsync("/api/v1/system/allowed-origins", new { OriginUrl = origin, Label = (string?)null });
        var id = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var resp = await f.AdminClient.DeleteAsync($"/api/v1/system/allowed-origins/{id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_allowed_origins__nonexistent_origin__returns_404()
    {
        var resp = await f.AdminClient.DeleteAsync($"/api/v1/system/allowed-origins/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Cache invalidation: the dynamic CORS policy reflects an added origin on the very next
    // request, with no app restart — exercised via a real preflight against a routed endpoint. ──

    [Fact]
    public async Task CORS_preflight__origin_added_via_admin_api__is_allowed_on_next_request_without_restart()
    {
        var origin = $"https://{Guid.NewGuid():N}.example.com";

        var before = await SendPreflightAsync(origin);
        Assert.False(before.Headers.Contains("Access-Control-Allow-Origin"));

        var createResp = await f.AdminClient.PostAsJsonAsync(
            "/api/v1/system/allowed-origins", new { OriginUrl = origin, Label = (string?)null });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var after = await SendPreflightAsync(origin);
        Assert.True(after.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal(origin, values!.Single());
    }

    private async Task<HttpResponseMessage> SendPreflightAsync(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/roles");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return await f.AnonClient.SendAsync(request);
    }
}
