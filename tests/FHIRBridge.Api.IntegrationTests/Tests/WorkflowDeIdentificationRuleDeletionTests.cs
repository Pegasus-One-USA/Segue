using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// DELETE /api/v1/workflows/{workflowId}/de-identification-rules — what the workflow builder calls when a
/// workflow's last De-identification step is removed. Deleting redaction rules needs transformationrules.delete;
/// view alone is refused before anything runs. What gets deleted (and what is refused) is covered by
/// FHIRBridge.UnitTests' WorkflowDeIdentificationRuleDeletionTests against the real service and repositories.
/// </summary>
[Collection("ApiTests")]
public sealed class WorkflowDeIdentificationRuleDeletionTests(ApiFixture f)
{
    private static string Url(Guid workflowId) => $"/api/v1/workflows/{workflowId}/de-identification-rules";

    [Fact]
    public async Task View_only_user_is_denied()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("transformationrules.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.DeleteAsync(Url(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_permission_reaches_the_endpoint_and_a_workflow_without_a_policy_deletes_nothing()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("transformationrules.delete");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.DeleteAsync(Url(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DeletionResult>();
        Assert.NotNull(body);
        Assert.Equal(0, body!.Deleted);
        Assert.Empty(body.UsedElsewhere);
    }

    private sealed record DeletionResult(int Deleted, string[] UsedElsewhere);
}
