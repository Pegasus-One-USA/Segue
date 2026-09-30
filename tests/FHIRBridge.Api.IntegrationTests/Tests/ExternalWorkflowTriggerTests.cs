using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class ExternalWorkflowTriggerTests(ApiFixture f)
{
    private const string ReturnUrl = "https://app.example.com/connect/callback";

    private async Task<(string ClientId, string Secret)> CreateClientWithReturnUrlAsync(string returnUrl = ReturnUrl)
    {
        var createResp = await f.AdminClient.PostAsJsonAsync(
            "/api/v1/api-clients", new { Name = $"Integration test client {Guid.NewGuid():N}" });
        var created = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("client").GetProperty("id").GetGuid();
        var clientId = created.GetProperty("client").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("plaintextSecret").GetString()!;

        var addUrlResp = await f.AdminClient.PostAsJsonAsync(
            $"/api/v1/api-clients/{id}/return-urls", new { Url = returnUrl, Label = (string?)null });
        Assert.Equal(HttpStatusCode.Created, addUrlResp.StatusCode);

        return (clientId, secret);
    }

    // ── POST /api/v1/workflows/external/run ──────────────────────────────────────

    [Fact]
    public async Task POST_external_run__valid_request_async_mode__redirects_to_returnUrl_with_Triggered()
    {
        var (clientId, secret) = await CreateClientWithReturnUrlAsync();
        var workflowId = await f.CreateWorkflowAsync();

        var resp = await f.AnonClient.PostAsync("/api/v1/workflows/external/run", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["workflow_id"] = workflowId.ToString(),
            ["return_url"] = ReturnUrl,
            ["mode"] = "async",
        }));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        Assert.StartsWith(ReturnUrl, location);
        Assert.Contains("status=Triggered", location);
    }

    [Fact]
    public async Task POST_external_run__wrong_secret__returns_401_with_no_redirect()
    {
        var (clientId, _) = await CreateClientWithReturnUrlAsync();
        var workflowId = await f.CreateWorkflowAsync();

        var resp = await f.AnonClient.PostAsync("/api/v1/workflows/external/run", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = "not-the-real-secret",
            ["workflow_id"] = workflowId.ToString(),
            ["return_url"] = ReturnUrl,
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Null(resp.Headers.Location);
    }

    [Fact]
    public async Task POST_external_run__unregistered_returnUrl__returns_401_with_no_redirect()
    {
        var (clientId, secret) = await CreateClientWithReturnUrlAsync();
        var workflowId = await f.CreateWorkflowAsync();

        var resp = await f.AnonClient.PostAsync("/api/v1/workflows/external/run", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["workflow_id"] = workflowId.ToString(),
            ["return_url"] = "https://attacker.example.net/",
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Null(resp.Headers.Location);
    }

    [Fact]
    public async Task POST_external_run__referer_present_but_mismatched__returns_401_with_no_redirect()
    {
        var (clientId, secret) = await CreateClientWithReturnUrlAsync();
        var workflowId = await f.CreateWorkflowAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/workflows/external/run")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["workflow_id"] = workflowId.ToString(),
                ["return_url"] = ReturnUrl,
            }),
        };
        request.Headers.Referrer = new Uri("https://attacker.example.net/");

        var resp = await f.AnonClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Null(resp.Headers.Location);
    }

    [Fact]
    public async Task POST_external_run__unknown_workflow__redirects_to_returnUrl_with_Failed()
    {
        var (clientId, secret) = await CreateClientWithReturnUrlAsync();

        var resp = await f.AnonClient.PostAsync("/api/v1/workflows/external/run", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["workflow_id"] = Guid.NewGuid().ToString(),
            ["return_url"] = ReturnUrl,
        }));

        // The returnUrl WAS validated, so this is safe to redirect to (unlike the credential/returnUrl/referer
        // failures above, which never redirect).
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("status=Failed", resp.Headers.Location!.ToString());
    }

    // ── POST /api/v1/workflows/external/list ─────────────────────────────────────

    [Fact]
    public async Task POST_external_list__valid_credentials__returns_workflows()
    {
        var (clientId, secret) = await CreateClientWithReturnUrlAsync();
        await f.CreateWorkflowAsync();

        var resp = await f.AnonClient.PostAsJsonAsync(
            "/api/v1/workflows/external/list", new { client_id = clientId, client_secret = secret });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
    }

    [Fact]
    public async Task POST_external_list__wrong_secret__returns_401()
    {
        var (clientId, _) = await CreateClientWithReturnUrlAsync();

        var resp = await f.AnonClient.PostAsJsonAsync(
            "/api/v1/workflows/external/list", new { client_id = clientId, client_secret = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
