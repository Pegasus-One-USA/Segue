using System.Net;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Verifies the OR-vendor-permission composite policy backing every auto-registered
/// HasPermission:sourceconnections.*/destinationconnections.* code (see
/// GenericConnectionPermissionRequirement/GenericConnectionPermissionAuthorizationHandler): a role
/// scoped to only a vendor-specific permission (e.g. "epic.view") can now reach the generic
/// Settings-page source/destination connection endpoints that used to require the flat
/// sourceconnections.*/destinationconnections.* permission alone. The plain generic permission keeps
/// working unchanged, a caller with neither is still denied, and actions with no vendor-specific
/// equivalent in the catalog (Export, Deactivate) are unaffected — they still require the generic
/// permission specifically, exactly as before this change.
///
/// Distinct from <see cref="SourceConnectionPermissionTests"/>, which covers the OLDER, separate
/// resource-based check on ConfigurationsController.AddSourceConnection/UpdateSourceConnection (the
/// request's own vendor resolved at runtime) — untouched by this change.
/// </summary>
[Collection("ApiTests")]
public sealed class GenericConnectionPermissionTests(ApiFixture f)
{
    // ── Source connections: GET /api/v1/source-connections (sourceconnections.view) ─────────────────

    [Fact]
    public async Task User_with_epicView__can_list_source_connections()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("epic.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/source-connections");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_generic_sourceconnectionsView__can_still_list_source_connections()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("sourceconnections.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/source-connections");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_no_relevant_permission__cannot_list_source_connections()
    {
        // "user.view" is unrelated to source connections — proves a permission the user genuinely
        // lacks (rather than a missing/misconfigured policy) is what's producing the 403.
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/source-connections");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Destination connections: GET /api/v1/destinations (destinationconnections.view) ─────────────

    [Fact]
    public async Task User_with_sqlserverView__can_list_destination_connections()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("sqlserver.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/destinations");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_generic_destinationconnectionsView__can_still_list_destination_connections()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("destinationconnections.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/destinations");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_no_relevant_permission__cannot_list_destination_connections()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/destinations");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Unaffected actions: Export/Deactivate have no vendor-specific equivalent in the catalog ─────
    // (no [DynamicSourceSystemPermission] site ever declares Export or Deactivate for any vendor), so
    // the OR-vendor branch can never match for them — they stay generic-only, exactly as before this
    // change. Authorization runs before the handler body, so a nonexistent id still cleanly
    // distinguishes 403 (denied before the lookup) from 404 (authorized, lookup ran, nothing found) —
    // the same trick PermissionEnforcementTests uses.

    [Fact]
    public async Task User_with_only_a_vendor_edit_permission__is_denied_downloading_a_private_key_pem()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("epic.edit");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync($"/api/v1/source-connections/{Guid.NewGuid()}/signing-key/private.pem");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_generic_sourceconnectionsExport__reaches_the_notfound_lookup()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("sourceconnections.export");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync($"/api/v1/source-connections/{Guid.NewGuid()}/signing-key/private.pem");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_only_a_vendor_permission__is_denied_deactivating_a_destination()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("sqlserver.edit");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsync($"/api/v1/destinations/{Guid.NewGuid()}/deactivate", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task User_with_generic_destinationconnectionsDeactivate__reaches_the_notfound_lookup()
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("destinationconnections.deactivate");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.PostAsync($"/api/v1/destinations/{Guid.NewGuid()}/deactivate", content: null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Admin sanity ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin__can_list_both_source_and_destination_connections()
    {
        var sourceResp = await f.AdminClient.GetAsync("/api/v1/source-connections");
        Assert.Equal(HttpStatusCode.OK, sourceResp.StatusCode);

        var destinationResp = await f.AdminClient.GetAsync("/api/v1/destinations");
        Assert.Equal(HttpStatusCode.OK, destinationResp.StatusCode);
    }
}
