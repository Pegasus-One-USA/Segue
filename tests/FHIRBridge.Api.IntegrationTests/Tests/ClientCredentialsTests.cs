using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class ClientCredentialsTests(ApiFixture f)
{
    // ── Admin CRUD: /api/v1/api-clients ──────────────────────────────────────────

    [Fact]
    public async Task POST_api_clients__valid_name__returns_201_with_plaintext_secret_once()
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/api-clients", new { Name = "Integration test client" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(doc.GetProperty("plaintextSecret").GetString()));
        Assert.StartsWith("cid_", doc.GetProperty("client").GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task POST_api_clients__unauthenticated__returns_401()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/api-clients", new { Name = "Should fail" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task POST_api_clients__non_admin__returns_403()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("configuration.write");
        var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsJsonAsync("/api/v1/api-clients", new { Name = "Should fail" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Token endpoint: /api/v1/oauth/token ──────────────────────────────────────

    [Fact]
    public async Task POST_token__valid_client_credentials__returns_200_bearer_token()
    {
        var (clientId, secret) = await CreateClientAsync();

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/oauth/token", new
        {
            grant_type = "client_credentials",
            client_id = clientId,
            client_secret = secret,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(doc.GetProperty("access_token").GetString()));
        Assert.Equal("Bearer", doc.GetProperty("token_type").GetString());
        Assert.True(doc.GetProperty("expires_in").GetInt32() > 0);
    }

    [Fact]
    public async Task POST_token__wrong_secret__returns_401_invalid_client()
    {
        var (clientId, _) = await CreateClientAsync();

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/oauth/token", new
        {
            grant_type = "client_credentials",
            client_id = clientId,
            client_secret = "not-the-real-secret",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_client", doc.GetProperty("error").GetString());
    }

    [Fact]
    public async Task POST_token__unknown_grant_type__returns_400()
    {
        var (clientId, secret) = await CreateClientAsync();

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/oauth/token", new
        {
            grant_type = "password",
            client_id = clientId,
            client_secret = secret,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_token__disabled_client__returns_401()
    {
        var createResp = await f.AdminClient.PostAsJsonAsync("/api/v1/api-clients", new { Name = "To be disabled" });
        var created = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("client").GetProperty("id").GetGuid();
        var clientId = created.GetProperty("client").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("plaintextSecret").GetString()!;

        await f.AdminClient.PutAsJsonAsync($"/api/v1/api-clients/{id}", new { Name = "To be disabled", IsEnabled = false });

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/oauth/token", new
        {
            grant_type = "client_credentials",
            client_id = clientId,
            client_secret = secret,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── The issued token triggers a workflow's /run, independent of IsPubliclyLaunchable ─────────

    [Fact]
    public async Task Token_from_client_credentials__triggers_run_on_a_non_publicly_launchable_workflow()
    {
        var (clientId, secret) = await CreateClientAsync();
        var tokenResp = await f.AnonClient.PostAsJsonAsync("/api/v1/oauth/token", new
        {
            grant_type = "client_credentials",
            client_id = clientId,
            client_secret = secret,
        });
        var accessToken = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("access_token").GetString()!;

        // A workflow with no nodes and IsPubliclyLaunchable = false — the portal RBAC path and the anonymous
        // standalone path would both refuse this; only the client-credentials caller should get through to
        // the point where the orchestrator itself decides the outcome (not a 401/403/404 before that).
        var workflowId = await f.CreateWorkflowAsync(isPubliclyLaunchable: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workflows/{workflowId}/run")
        {
            Content = JsonContent.Create(new { patientId = (string?)null, patientSearchCriteria = (string?)null, callerId = (string?)null }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var resp = await f.AnonClient.SendAsync(request);

        // Not 401/403/404: the client-credentials caller cleared the auth gate. Whatever the orchestrator
        // does next (this workflow has no nodes) is out of scope for this test.
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<(string ClientId, string Secret)> CreateClientAsync()
    {
        var createResp = await f.AdminClient.PostAsJsonAsync(
            "/api/v1/api-clients", new { Name = $"Integration test client {Guid.NewGuid():N}" });
        var created = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement;
        return (
            created.GetProperty("client").GetProperty("clientId").GetString()!,
            created.GetProperty("plaintextSecret").GetString()!);
    }
}
