using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Verifies the resource-based permission check on ConfigurationsController.AddSourceConnection /
/// UpdateSourceConnection: the required permission group is resolved from the request's
/// SourceSystemType at runtime (via SourceSystemPermissionGroups), not from a static
/// [StandardPermission] attribute — a user holding only "epic.edit" can manage an Epic source
/// connection but not a Cerner one, and vice versa.
/// </summary>
[Collection("ApiTests")]
public sealed class SourceConnectionPermissionTests(ApiFixture f)
{
    private static object MinimalConnectionBody(string sourceSystemType, string name) => new
    {
        Name             = name,
        SourceSystemType = sourceSystemType,
        BaseUrl          = "https://example.test/fhir",
        Authentication   = new { AuthenticationType = "None", Scopes = Array.Empty<string>() }
    };

    // Epic connections are validated far more strictly than other vendors
    // (ConfigurationService.ValidateEpicSourceConnection) — a full SMART Backend Services payload is
    // required, or the request 400s before authorization is even relevant to the test.
    private static object EpicConnectionBody(string name) => new
    {
        Name             = name,
        SourceSystemType = "Epic",
        BaseUrl          = "https://example.test/fhir",
        Authentication   = new
        {
            AuthenticationType      = "SmartBackendServices",
            ClientId                = "test-client-id",
            TokenEndpoint           = "https://example.test/oauth2/token",
            Scopes                  = new[] { "system/*.read" },
            KeyId                   = "test-key-id",
            PrivateKeyKeyVaultName  = "test-vault",
            PrivateKeySecretName    = "test-secret"
        }
    };

    /// <summary>Creates a user whose only role grants the given permission, and logs them in.</summary>
    private async Task<string> CreateUserWithOnlyPermissionAndLoginAsync(string permissionName)
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync(permissionName);
        return jwt;
    }

    [Fact]
    public async Task User_with_epic_edit__can_create_an_Epic_source_connection()
    {
        var jwt = await CreateUserWithOnlyPermissionAndLoginAsync("epic.edit");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/source-connections",
            EpicConnectionBody($"Epic-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_epic_edit__cannot_create_a_Cerner_source_connection()
    {
        var jwt = await CreateUserWithOnlyPermissionAndLoginAsync("epic.edit");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/source-connections",
            MinimalConnectionBody("Cerner", $"Cerner-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_cerner_edit__can_create_a_Cerner_source_connection_but_not_Epic()
    {
        var jwt = await CreateUserWithOnlyPermissionAndLoginAsync("cerner.edit");
        using var client = f.CreateAuthenticatedClient(jwt);

        var cernerResp = await client.PostAsJsonAsync(
            "/api/v1/source-connections",
            MinimalConnectionBody("Cerner", $"Cerner-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Created, cernerResp.StatusCode);

        var epicResp = await client.PostAsJsonAsync(
            "/api/v1/source-connections",
            MinimalConnectionBody("Epic", $"Epic-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Forbidden, epicResp.StatusCode);
    }

    [Fact]
    public async Task User_with_no_vendor_permission__cannot_create_a_generic_source_connection()
    {
        // "user.view" is unrelated to source connections — proves a permission the user genuinely
        // lacks (rather than a missing/misconfigured policy) is what's producing the 403.
        var jwt = await CreateUserWithOnlyPermissionAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/source-connections",
            MinimalConnectionBody("Sample", $"Sample-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin__can_create_a_source_connection_of_any_vendor()
    {
        var resp = await f.AdminClient.PostAsJsonAsync(
            "/api/v1/source-connections",
            MinimalConnectionBody("Cerner", $"Cerner-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    // Allscripts has no dedicated permission-checking code anywhere — PermissionGroupCode.Allscripts
    // is the only line added for it (matching SourceSystemType.Allscripts by name). If
    // "allscripts.edit" weren't auto-discovered, this permission simply wouldn't exist to grant.

    [Fact]
    public async Task User_with_allscripts_edit__can_create_an_Allscripts_source_connection_but_not_Epic()
    {
        var jwt = await CreateUserWithOnlyPermissionAndLoginAsync("allscripts.edit");
        using var client = f.CreateAuthenticatedClient(jwt);

        var allscriptsResp = await client.PostAsJsonAsync(
            "/api/v1/source-connections",
            MinimalConnectionBody("Allscripts", $"Allscripts-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Created, allscriptsResp.StatusCode);

        var epicResp = await client.PostAsJsonAsync(
            "/api/v1/source-connections",
            MinimalConnectionBody("Epic", $"Epic-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Forbidden, epicResp.StatusCode);
    }
}
