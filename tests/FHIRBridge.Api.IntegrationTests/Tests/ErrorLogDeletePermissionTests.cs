using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Permanently deleting error-log entries needs governance.delete, which is separate from governance.write
/// (settings changes, hide-old-errors). A caller with write but not delete must be refused on BOTH delete paths
/// before anything runs; the refusal itself is recorded by GovernanceAuditingAuthorizationMiddlewareResultHandler
/// (an AuthorizationLog row with the path and the missing permission).
/// </summary>
[Collection("ApiTests")]
public sealed class ErrorLogDeletePermissionTests(ApiFixture f)
{
    [Fact]
    public async Task Write_only_user_is_denied_the_dashboard_delete()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("governance.write");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsJsonAsync("/api/v1/operations/errors/delete", new { mode = "all" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Write_only_user_is_denied_the_settings_purge()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("governance.write");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsync("/api/v1/operations/error-log-settings/purge", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_permission_gets_past_authorization_on_the_dashboard_delete()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("governance.delete");
        using var client = f.CreateAuthenticatedClient(jwt);

        // An invalid mode is rejected with 400 AFTER authorization and BEFORE any delete, so this proves the permission
        // is accepted without destroying anything.
        var resp = await client.PostAsJsonAsync("/api/v1/operations/errors/delete", new { mode = "nonsense" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Write_only_user_cannot_switch_on_auto_clear()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("governance.write");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PutAsJsonAsync("/api/v1/operations/error-log-settings", new
        {
            captureSeverities = new[] { "Error" },
            captureCategories = new[] { "Unknown" },
            autoClearEnabled = true,
            retentionDays = 365,
            workflowDebugDetail = "Steps",
        });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
